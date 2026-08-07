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
    /// Per-radio connection-metadata cache (V1 schema). On the 4.1 line this
    /// class is a write-only backport of the 4.2-line discovery-cascade cache:
    /// every successful Connect populates radioConnectionCacheV1.xml in the
    /// JJ Flex config directory so that when the same machine later runs a
    /// 4.2-line build, Rung 1a CachedLanIp can short-circuit UDP discovery
    /// from the very first launch. Lookup/GetAllEntries are unused on 4.1 but
    /// kept to lock schema parity with track/flexlib-42 — do not edit field
    /// names or types without coordinating a V2 file.
    ///
    /// The WAN fields (WanIp, PublicTlsPort, RequiresHolePunch, IsPortForwardOn)
    /// are LOCAL ONLY per project_no_silent_phone_home.md. They must never
    /// appear in trace exports, crash reports, or Data Provider sync. When the
    /// support-package format is finalized, add WanIp + PublicTlsPort to the
    /// redaction list.
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

        public IReadOnlyList<RadioConnectionCacheEntry> GetAllEntries()
        {
            lock (_sync)
            {
                return _data.Entries.ToList();
            }
        }

        /// <summary>
        /// Remember the radio list a SmartLink account last returned, so the
        /// selector can PAINT that account's radios the instant it opens
        /// instead of showing an empty box for the seconds a TLS session takes.
        ///
        /// <para>This is a fast paint, never an authority: entries recorded here
        /// carry <see cref="AccountRadioListEntry.FetchedUtc"/> so the UI can
        /// say "last known radios for &lt;account&gt;, refreshing" and age-announce
        /// stale ones. Nothing may be CONNECTED to from this record without a
        /// live fetch in flight — provenance beats TTL, and a cached row that
        /// pretends to be live is exactly the lie this project does not tell.</para>
        ///
        /// <para>Stored in the same file rather than a second store, as an
        /// additional top-level element. XmlSerializer ignores elements it does
        /// not know, so a 4.2-line build reading this file skips
        /// <c>AccountLists</c> and the per-radio Entries schema stays parity-locked.</para>
        /// </summary>
        public void RecordAccountRadioList(string accountEmail, IEnumerable<Radio> radios)
        {
            if (string.IsNullOrWhiteSpace(accountEmail)) return;
            if (radios == null) return;

            lock (_sync)
            {
                var entry = _data.AccountLists.FirstOrDefault(a =>
                    string.Equals(a.AccountEmail, accountEmail, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    entry = new AccountRadioListEntry { AccountEmail = accountEmail };
                    _data.AccountLists.Add(entry);
                }

                entry.FetchedUtc = DateTime.UtcNow;
                entry.Radios = radios
                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Serial))
                    .Select(r => new CachedRadioListItem
                    {
                        Serial = r.Serial,
                        Nickname = r.Nickname ?? "",
                        Model = r.Model ?? "",
                    })
                    .ToList();

                Save();
            }
        }

        /// <summary>
        /// The last radio list seen for this account, or null when the account
        /// has never completed a SmartLink list pass on this machine.
        /// </summary>
        public AccountRadioListEntry LookupAccountRadioList(string accountEmail)
        {
            if (string.IsNullOrWhiteSpace(accountEmail)) return null;
            lock (_sync)
            {
                return _data.AccountLists.FirstOrDefault(a =>
                    string.Equals(a.AccountEmail, accountEmail, StringComparison.OrdinalIgnoreCase));
            }
        }

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
                    entry.LanIp = radio.IP?.ToString() ?? entry.LanIp;
                    entry.LanLastSeenUtc = nowUtc;
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

        /// <summary>
        /// Per-SmartLink-account radio lists (queue-burn Track E). Additive:
        /// older readers ignore the element, older files deserialize to an
        /// empty list. Do NOT fold these into <see cref="Entries"/> — Entries
        /// is schema-parity-locked with the 4.2 discovery cascade.
        /// </summary>
        public List<AccountRadioListEntry> AccountLists { get; set; } = new();
    }

    /// <summary>
    /// One SmartLink account's last known radio list, with the timestamp that
    /// makes its age announceable. LOCAL ONLY, like the WAN fields above: the
    /// account email is personally identifying and never leaves the machine.
    /// </summary>
    public sealed class AccountRadioListEntry
    {
        public string AccountEmail { get; set; } = "";
        public DateTime FetchedUtc { get; set; }
        public List<CachedRadioListItem> Radios { get; set; } = new();
    }

    /// <summary>One radio as it appeared in a cached account list. Deliberately
    /// display-only — no addresses, ports, or handles, because nothing here may
    /// ever be used to open a connection.</summary>
    public sealed class CachedRadioListItem
    {
        public string Serial { get; set; } = "";
        public string Nickname { get; set; } = "";
        public string Model { get; set; } = "";
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
