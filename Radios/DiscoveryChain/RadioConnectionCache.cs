using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// Per-radio connection-metadata cache. Backs the discovery fallback chain
    /// (Rung 1a cached LAN IP, Rung 1b cached SmartLink target, Rung 1c LAN IP
    /// history). Keyed by radio serial; persists to
    /// <c>radioConnectionCacheV1.xml</c> in the JJ Flex config directory.
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

        /// <summary>
        /// LRU depth for per-radio LAN-IP history. Q6 ACK 2026-05-08: 5 IPs per radio.
        /// At 5, single-home users see one entry (no marginal cost), roaming /
        /// multi-network users get the IP-rotation coverage Stream 5 §3.14 motivates.
        /// </summary>
        public const int MaxHistoryDepth = 5;

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
        /// Returns a snapshot of the LAN-IP history for the given radio,
        /// most-recent-first. Empty if no entry or no history captured yet.
        /// Used by Rung 1c (cached LAN IP history) for parallel probing.
        /// </summary>
        public IReadOnlyList<HistoricalLanIp> GetLanIpHistory(string serial)
        {
            if (string.IsNullOrEmpty(serial))
                return Array.Empty<HistoricalLanIp>();
            lock (_sync)
            {
                var entry = _data.Entries.FirstOrDefault(e =>
                    string.Equals(e.Serial, serial, StringComparison.OrdinalIgnoreCase));
                if (entry == null || entry.LanIpHistory == null)
                    return Array.Empty<HistoricalLanIp>();
                return entry.LanIpHistory.ToList();
            }
        }

        /// <summary>
        /// Returns a snapshot of all cached entries. Used by the manual rig-selector
        /// path to populate the dialog from cache before UDP discovery completes.
        /// </summary>
        public IReadOnlyList<RadioConnectionCacheEntry> GetAllEntries()
        {
            lock (_sync)
            {
                return _data.Entries.ToList();
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
                // Use FlexLib's own packer so the cached string round-trips through
                // FlexVersion.TryParse on the read side. radio.Version.ToString()
                // gave us decimal-of-ulong (e.g. "1127020893346674"), which TryParse
                // rejects — manifested as firmware version 0.0.0.0 in Don's R6 trace.
                entry.Version = Flex.Util.FlexVersion.ToString(radio.Version);

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
                    var ip = radio.IP?.ToString();
                    if (!string.IsNullOrEmpty(ip))
                    {
                        entry.LanIp = ip;
                        entry.LanLastSeenUtc = nowUtc;
                        AppendLanIpHistory(entry, ip, nowUtc);
                    }
                    else
                    {
                        entry.LanLastSeenUtc = nowUtc;
                    }
                }

                Save();
            }

            // Tag the active trace session with this radio's metadata so the
            // session manifest entry's connection_target field is populated
            // automatically — no caller plumbing needed. Per Sprint 29 Track A
            // Phase 2 / memory/project_trace_persistence_design.md. Safe no-op
            // when tracing is off (TraceSessionContext.Current is null).
            try
            {
                TraceSessionContext.SetConnectionTarget(
                    serial: radio.Serial,
                    nickname: radio.Nickname ?? "",
                    smartlinkAccount: "",  // populated separately by SmartLink layer when applicable
                    ip: radio.IP?.ToString() ?? "");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"RadioConnectionCache: SetConnectionTarget failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Push the given IP to the front of the radio's LAN-IP history,
        /// dedup-by-IP-string, and LRU-cap at <see cref="MaxHistoryDepth"/>.
        /// Captures the current NLM-style network identity into the new
        /// history entry so Rung 1c can NLM-gate its probe set per Q5 ACK
        /// 2026-05-08. Caller must hold _sync.
        /// </summary>
        private static void AppendLanIpHistory(RadioConnectionCacheEntry entry, string ip, DateTime nowUtc)
        {
            entry.LanIpHistory ??= new List<HistoricalLanIp>();

            // Dedup: drop any prior entry for the same IP. The new entry will
            // be inserted at position 0, preserving LRU order with most-recent-first.
            entry.LanIpHistory.RemoveAll(h =>
                string.Equals(h.Ip, ip, StringComparison.OrdinalIgnoreCase));

            entry.LanIpHistory.Insert(0, new HistoricalLanIp
            {
                Ip = ip,
                LastSeenUtc = nowUtc,
                NetworkIdentityGuid = NetworkIdentity.GetCurrentNetworkId()
            });

            while (entry.LanIpHistory.Count > MaxHistoryDepth)
            {
                entry.LanIpHistory.RemoveAt(entry.LanIpHistory.Count - 1);
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

        /// <summary>
        /// N-deep LAN-IP history per radio, most-recent-first. Drives Rung 1c
        /// (cached LAN IP history) per docs/planning/design/discovery-fallback-chain-v3.md.
        /// Each entry carries the IP, the UTC timestamp it was last seen at,
        /// and (when populated) the NLM network-identity GUID at that time.
        /// LRU-capped at <see cref="RadioConnectionCache.MaxHistoryDepth"/>;
        /// duplicates are deduplicated on insert. Backward-compatible additive
        /// schema: older XML files just get an empty list on load.
        /// </summary>
        public List<HistoricalLanIp> LanIpHistory { get; set; } = new();

        // LOCAL ONLY — never export. See class doc comment.
        public string WanIp { get; set; } = "";
        public int PublicTlsPort { get; set; }
        public bool RequiresHolePunch { get; set; }
        public bool IsPortForwardOn { get; set; }
        public bool IsRemote { get; set; }
        public DateTime WanLastSeenUtc { get; set; }
    }

    /// <summary>
    /// A single LAN-IP-history entry: the IP we saw the radio at, when, and
    /// (when captured) the NLM network identity GUID for that observation.
    /// NetworkIdentityGuid is empty in Phase 1 (Rung 1c initial ship); the
    /// NLM-gating commit populates it. An empty GUID means "no network
    /// identity captured" and the NLM gate falls through (try the entry).
    /// </summary>
    public sealed class HistoricalLanIp
    {
        public string Ip { get; set; } = "";
        public DateTime LastSeenUtc { get; set; }
        public string NetworkIdentityGuid { get; set; } = "";
    }
}
