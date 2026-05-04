using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Flex.Smoothlake.FlexLib;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// Per-radio connection-metadata cache. Backs the discovery fallback chain
    /// (Rung 1a cached LAN IP, Rung 1b cached SmartLink target). Keyed by radio
    /// serial; persists to <c>radioConnectionCacheV1.xml</c> in the JJ Flex
    /// config directory.
    ///
    /// The WAN fields (WanIp, PublicTlsPort, RequiresHolePunch, IsPortForwardOn)
    /// are LOCAL ONLY per <c>project_no_silent_phone_home.md</c>. They must
    /// never appear in trace exports, crash reports, or Data Provider sync.
    /// When the support-package format is finalized, add WanIp + PublicTlsPort
    /// to the redaction list.
    /// </summary>
    public sealed class RadioConnectionCache
    {
        private const string FileName = "radioConnectionCacheV1.xml";
        private readonly string _filePath;
        private RadioConnectionCacheFile _data;
        private readonly object _sync = new();

        public RadioConnectionCache(string configDirectory)
        {
            _filePath = Path.Combine(configDirectory, FileName);
            _data = Load(_filePath);
        }

        public RadioConnectionCacheEntry Lookup(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return null;
            lock (_sync)
            {
                return _data.Entries.FirstOrDefault(e =>
                    string.Equals(e.Serial, serial, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Records the given radio's LAN/WAN connection state into the cache.
        /// Called after a successful FlexLib Connect() so future startups can
        /// short-circuit discovery.
        /// </summary>
        public void RecordConnectedRadio(Radio radio)
        {
            if (radio == null || string.IsNullOrEmpty(radio.Serial)) return;

            lock (_sync)
            {
                var entry = _data.Entries.FirstOrDefault(e =>
                    string.Equals(e.Serial, radio.Serial, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    entry = new RadioConnectionCacheEntry { Serial = radio.Serial };
                    _data.Entries.Add(entry);
                }

                entry.Nickname = radio.Nickname ?? entry.Nickname ?? "";
                entry.Model = radio.Model ?? entry.Model ?? "";
                entry.Version = radio.Version.ToString();

                var nowUtc = DateTime.UtcNow;
                if (radio.IsWan)
                {
                    entry.WanIp = radio.IP?.ToString() ?? entry.WanIp;
                    entry.PublicTlsPort = radio.PublicTlsPort;
                    entry.RequiresHolePunch = radio.RequiresHolePunch;
                    entry.IsPortForwardOn = radio.IsPortForwardOn;
                    entry.IsRemote = true;
                    entry.WanLastSeenUtc = nowUtc;
                }
                else
                {
                    entry.LanIp = radio.IP?.ToString() ?? entry.LanIp;
                    entry.LanLastSeenUtc = nowUtc;
                }

                Save();
            }
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var fs = File.Create(_filePath);
                var ser = new XmlSerializer(typeof(RadioConnectionCacheFile));
                ser.Serialize(fs, _data);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"RadioConnectionCache.Save failed: {ex.Message}");
            }
        }

        private static RadioConnectionCacheFile Load(string filePath)
        {
            if (!File.Exists(filePath)) return new RadioConnectionCacheFile();
            try
            {
                using var fs = File.OpenRead(filePath);
                var ser = new XmlSerializer(typeof(RadioConnectionCacheFile));
                return (RadioConnectionCacheFile)ser.Deserialize(fs) ?? new RadioConnectionCacheFile();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"RadioConnectionCache.Load failed: {ex.Message}");
                return new RadioConnectionCacheFile();
            }
        }
    }

    [XmlRoot("RadioConnectionCache")]
    public sealed class RadioConnectionCacheFile
    {
        public List<RadioConnectionCacheEntry> Entries { get; set; } = new();
    }

    public sealed class RadioConnectionCacheEntry
    {
        public string Serial { get; set; } = "";
        public string Nickname { get; set; } = "";
        public string Model { get; set; } = "";
        public string Version { get; set; } = "";

        public string LanIp { get; set; } = "";
        public DateTime LanLastSeenUtc { get; set; }

        // LOCAL ONLY — never export. See class doc comment.
        public string WanIp { get; set; } = "";
        public int PublicTlsPort { get; set; }
        public bool RequiresHolePunch { get; set; }
        public bool IsPortForwardOn { get; set; }
        public bool IsRemote { get; set; }
        public DateTime WanLastSeenUtc { get; set; }
    }
}
