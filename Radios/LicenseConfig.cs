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
        /// When true, enforce TX parameters based on published rules (USB mode on 60m channels,
        /// 100W power cap, TX lockout outside channels/digi segment). When false, no restrictions.
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
        /// The four classes the operator form and Settings offer, in Settings'
        /// order (Noel, 2026-11-23: the classes an operator can hold today;
        /// Technician, General and Extra are issued, Advanced is still held and
        /// renewed). <see cref="Bands.Licenses"/> keeps all six on purpose
        /// (historical), and none of the others is offered.
        /// </summary>
        public static readonly IReadOnlyList<Bands.Licenses> OfferedClasses = new[]
        {
            Bands.Licenses.extra, Bands.Licenses.advanced, Bands.Licenses.general, Bands.Licenses.technition,
        };

        /// <summary>
        /// The class an operator the form creates starts on (Noel,
        /// 2026-11-23: "I'd start on General"). Only a NEW operator: an
        /// existing one with no licence file still reads Extra, the
        /// deserialization default, so nothing on disk changes meaning.
        /// </summary>
        public const Bands.Licenses NewOperatorClass = Bands.Licenses.general;

        /// <summary>
        /// A held class as one of the four offered: a held Novice reads as
        /// Technician (the band table gives them the same privileges), and
        /// the four read as themselves. A stored <c>none</c> is not a class
        /// anyone holds and reads as Extra, which is what Settings has always
        /// shown for it; nothing stored is changed by reading it.
        /// </summary>
        public static Bands.Licenses ShownClass(Bands.Licenses stored) => stored switch
        {
            Bands.Licenses.novice => Bands.Licenses.technition,
            Bands.Licenses.technition or Bands.Licenses.general or Bands.Licenses.advanced or Bands.Licenses.extra => stored,
            _ => Bands.Licenses.extra,
        };

        /// <summary>The spoken label for one of the offered classes, Settings' own words.</summary>
        public static string ClassLabel(Bands.Licenses offered) => ShownClass(offered) switch
        {
            Bands.Licenses.advanced => Lexicon.Get("settings.license.class_advanced"),
            Bands.Licenses.general => Lexicon.Get("settings.license.class_general"),
            Bands.Licenses.technition => Lexicon.Get("settings.license.class_technician"),
            _ => Lexicon.Get("settings.license.class_extra"),
        };

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
