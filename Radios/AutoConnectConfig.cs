using System;
using System.IO;
using System.Xml.Serialization;

namespace Radios
{
    /// <summary>
    /// Unified auto-connect configuration for both local and remote radios.
    /// Stored per-operator as {OperatorName}_autoConnect.xml in the config directory.
    /// Only ONE radio can have auto-connect enabled at a time.
    /// </summary>
    [XmlRoot("AutoConnectConfig")]
    public class AutoConnectConfig
    {
        /// <summary>
        /// Whether auto-connect is enabled for this specific radio.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// The radio's serial number (unique identifier).
        /// </summary>
        public string RadioSerial { get; set; } = "";

        /// <summary>
        /// Human-readable radio name (e.g., "Don's 6600").
        /// </summary>
        public string RadioName { get; set; } = "";

        /// <summary>
        /// True if this is a remote (SmartLink) radio, false if local.
        /// </summary>
        public bool IsRemote { get; set; }

        /// <summary>
        /// For remote radios: the SmartLink account email used to connect.
        /// Empty for local radios.
        /// </summary>
        public string SmartLinkAccountEmail { get; set; } = "";

        /// <summary>
        /// Whether to use low bandwidth mode for this connection.
        /// </summary>
        public bool LowBandwidth { get; set; }

        /// <summary>
        /// Master switch: when false, auto-connect is skipped even if Enabled is true.
        /// Allows user to temporarily disable auto-connect without losing radio-specific settings.
        /// </summary>
        public bool GlobalAutoConnectEnabled { get; set; } = true;

        /// <summary>
        /// Returns true if auto-connect should actually occur at startup.
        /// Both GlobalAutoConnectEnabled and Enabled must be true, and a radio must be configured.
        /// </summary>
        [XmlIgnore]
        public bool ShouldAutoConnect =>
            GlobalAutoConnectEnabled && Enabled && !string.IsNullOrEmpty(RadioSerial);

        /// <summary>
        /// Loads the auto-connect configuration for the specified operator.
        /// Returns a default (empty) config if file doesn't exist or can't be read.
        /// </summary>
        /// <param name="configDirectory">Base config directory (e.g., BaseConfigDir)</param>
        /// <param name="operatorName">Unique operator name (from PersonalData.UniqueOpName)</param>
        public static AutoConnectConfig Load(string configDirectory, string operatorName)
        {
            var filePath = GetFilePath(configDirectory, operatorName);

            if (!File.Exists(filePath))
            {
                return new AutoConnectConfig();
            }

            try
            {
                using var fs = File.OpenRead(filePath);
                var serializer = new XmlSerializer(typeof(AutoConnectConfig));
                var config = (AutoConnectConfig?)serializer.Deserialize(fs);
                return config ?? new AutoConnectConfig();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"AutoConnectConfig.Load failed: {ex.Message}");
                return new AutoConnectConfig();
            }
        }

        /// <summary>
        /// Saves this configuration to disk.
        /// </summary>
        /// <param name="configDirectory">Base config directory</param>
        /// <param name="operatorName">Unique operator name</param>
        public void Save(string configDirectory, string operatorName)
        {
            var filePath = GetFilePath(configDirectory, operatorName);

            try
            {
                // Ensure directory exists
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var fs = File.Create(filePath);
                var serializer = new XmlSerializer(typeof(AutoConnectConfig));
                serializer.Serialize(fs, this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"AutoConnectConfig.Save failed: {ex.Message}");
                throw; // Re-throw so caller knows save failed
            }
        }

        /// <summary>
        /// Sets this configuration to auto-connect to the specified radio.
        /// Clears any previous radio settings.
        /// </summary>
        /// <param name="serial">Radio serial number</param>
        /// <param name="name">Radio display name</param>
        /// <param name="isRemote">True if remote/SmartLink radio</param>
        /// <param name="smartLinkEmail">SmartLink account email (for remote radios)</param>
        /// <param name="lowBandwidth">Whether to use low bandwidth mode</param>
        public void SetAutoConnectRadio(string serial, string name, bool isRemote,
            string smartLinkEmail = "", bool lowBandwidth = false)
        {
            Enabled = true;
            RadioSerial = serial ?? "";
            RadioName = name ?? "";
            IsRemote = isRemote;
            SmartLinkAccountEmail = isRemote ? (smartLinkEmail ?? "") : "";
            LowBandwidth = lowBandwidth;
        }

        /// <summary>
        /// Clears the auto-connect radio setting (but preserves GlobalAutoConnectEnabled).
        /// </summary>
        public void ClearAutoConnectRadio()
        {
            Enabled = false;
            RadioSerial = "";
            RadioName = "";
            IsRemote = false;
            SmartLinkAccountEmail = "";
            LowBandwidth = false;
        }

        /// <summary>
        /// Checks if a different radio currently has auto-connect enabled.
        /// Used to show confirmation dialog before switching.
        /// </summary>
        /// <param name="newRadioSerial">The serial of the radio user wants to set</param>
        /// <returns>True if a DIFFERENT radio is currently configured</returns>
        public bool HasDifferentAutoConnectRadio(string newRadioSerial)
        {
            return Enabled &&
                   !string.IsNullOrEmpty(RadioSerial) &&
                   RadioSerial != newRadioSerial;
        }

        /// <summary>
        /// Gets a user-friendly description of the current auto-connect status.
        /// </summary>
        public string GetStatusDescription()
        {
            if (!Enabled || string.IsNullOrEmpty(RadioSerial))
            {
                return "No auto-connect configured";
            }

            var radioDesc = string.IsNullOrEmpty(RadioName) ? RadioSerial : RadioName;
            var remoteDesc = IsRemote ? " (Remote)" : " (Local)";
            var bwDesc = LowBandwidth ? ", Low Bandwidth" : "";
            var globalDesc = GlobalAutoConnectEnabled ? "" : " [Temporarily Disabled]";

            return $"Auto-connect: {radioDesc}{remoteDesc}{bwDesc}{globalDesc}";
        }

        private static string GetFilePath(string configDirectory, string operatorName)
        {
            return Path.Combine(configDirectory, $"{operatorName}_autoConnectV2.xml");
        }
    }
}
