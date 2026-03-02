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

                using var fs = File.Create(filePath);
                var serializer = new XmlSerializer(typeof(LicenseConfig));
                serializer.Serialize(fs, this);
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
