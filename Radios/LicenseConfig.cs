using System;
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

        public void Save(string configDirectory, string operatorName)
        {
            var filePath = GetFilePath(configDirectory, operatorName);

            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var serializer = new XmlSerializer(typeof(LicenseConfig));
                AtomicFile.Write(filePath, fs => serializer.Serialize(fs, this));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"LicenseConfig.Save failed: {ex.Message}");
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
