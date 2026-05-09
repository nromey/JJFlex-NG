using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Radios.DiscoveryChain.Providers
{
    /// <summary>
    /// Third-party config scrape provider for SmartSDR (the primary candidate
    /// — Stream 3 says SmartSDR alone covers ~70% of new-user installs).
    ///
    /// Files inspected (under <c>%APPDATA%\FlexRadio Systems\</c>):
    /// <list type="bullet">
    ///   <item><c>SSDR.settings</c> — main settings (.NET application
    ///   settings XML format).</item>
    ///   <item><c>CAT.settings</c> — DDUtil-style CAT config.</item>
    ///   <item><c>DAX.settings</c> — DAX (Digital Audio eXchange) config.</item>
    /// </list>
    ///
    /// Phase 2 implementation strategy:
    /// <list type="number">
    ///   <item>Existence check on each candidate file.</item>
    ///   <item>Read with <c>FileShare.ReadWrite</c> so a running SmartSDR
    ///   doesn't lock the read.</item>
    ///   <item>IPv4 regex sweep — schema-aware XML parse defers to Phase 2.x
    ///   when a real <c>SSDR.settings</c> file is available to inspect.
    ///   Regex sweep + RFC1918 filter catches the IP regardless of its
    ///   exact XML element name.</item>
    ///   <item>Filter loopback / multicast / broadcast / non-RFC1918.</item>
    ///   <item>Dedup IPs across files, attribute by file name in
    ///   <see cref="ThirdPartyIpCandidate.SourceDetail"/>.</item>
    /// </list>
    ///
    /// Per docs/planning/design/discovery-fallback-chain-v3.md §3 Rung 1.7.
    /// Privacy posture per Stream 3: trace records paths checked + IP-token
    /// counts, never raw file contents.
    /// </summary>
    public sealed class SmartSdrConfigProvider : IThirdPartyConfigProvider
    {
        private static readonly string[] CandidateFiles =
        {
            "SSDR.settings",
            "CAT.settings",
            "DAX.settings",
            "filter.txt"
        };

        private static readonly Regex Ipv4Pattern = new(
            @"\b(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)" +
            @"(?:\.(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)){3}\b",
            RegexOptions.Compiled);

        public string Name => "SmartSDR";
        public bool Enabled { get; init; } = true;
        public string OutcomeTag { get; private set; } = "";

        public List<ThirdPartyIpCandidate> Discover()
        {
            var results = new List<ThirdPartyIpCandidate>();

            if (!Enabled)
            {
                OutcomeTag = "disabled";
                return results;
            }

            string baseDir;
            try
            {
                baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FlexRadio Systems");
            }
            catch
            {
                OutcomeTag = "no_files";
                return results;
            }

            if (!Directory.Exists(baseDir))
            {
                OutcomeTag = "no_files";
                return results;
            }

            int filesFound = 0;
            int filesParsed = 0;
            int totalTokens = 0;
            bool anyParseError = false;

            foreach (var fileName in CandidateFiles)
            {
                string fullPath = Path.Combine(baseDir, fileName);
                if (!File.Exists(fullPath)) continue;
                filesFound++;

                string content;
                try
                {
                    using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    content = reader.ReadToEnd();
                    filesParsed++;
                }
                catch
                {
                    anyParseError = true;
                    continue;
                }

                MatchCollection matches = Ipv4Pattern.Matches(content);
                totalTokens += matches.Count;

                foreach (Match m in matches)
                {
                    string ipText = m.Value;
                    if (!IsRoutableLanCandidate(ipText)) continue;
                    results.Add(new ThirdPartyIpCandidate
                    {
                        Ip = ipText,
                        Source = Name,
                        SourceDetail = fileName
                    });
                }
            }

            // Dedup keeping the first attribution.
            results = results
                .GroupBy(c => c.Ip, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (filesFound == 0) OutcomeTag = "no_files";
            else if (results.Count > 0) OutcomeTag = "found";
            else if (anyParseError) OutcomeTag = "parse_error";
            else OutcomeTag = "no_ips";

            return results;
        }

        /// <summary>
        /// Filter for "this IPv4 string could plausibly be a Flex radio's LAN
        /// address." Drops loopback, multicast, broadcast, link-local, and
        /// non-RFC1918 (no public-internet IPs in cache files for a LAN-side
        /// radio). Keeps RFC1918 and reserved-but-routable address families.
        /// </summary>
        private static bool IsRoutableLanCandidate(string ipText)
        {
            if (!System.Net.IPAddress.TryParse(ipText, out var ip)) return false;
            byte[] bytes = ip.GetAddressBytes();
            if (bytes.Length != 4) return false;

            // 0.0.0.0 / 255.255.255.255
            if (bytes[0] == 0) return false;
            if (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255) return false;
            // 127.0.0.0/8 loopback
            if (bytes[0] == 127) return false;
            // 224.0.0.0/4 multicast (224-239)
            if (bytes[0] >= 224 && bytes[0] <= 239) return false;
            // 240.0.0.0/4 reserved future-use (240-255)
            if (bytes[0] >= 240) return false;

            // RFC1918 + APIPA — accept these as plausible LAN IPs.
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && (bytes[1] >= 16 && bytes[1] <= 31)) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 APIPA (link-local — rare but plausible in self-assigned setups)
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            // Reject everything else (public IPs, CGNAT 100.64/10, etc.).
            // CGNAT could be valid for some VPN setups but is rare enough
            // to require explicit user override; not a default-accept case.
            return false;
        }
    }
}
