using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JJTrace
{
    /// <summary>
    /// A previous run's rotated part files that no durable record claims,
    /// grouped by the boot stamp in their names and filed under one inventory
    /// identity.
    /// </summary>
    public sealed class LeftoverChainAdoption
    {
        /// <summary>The inventory session the parts were filed under. Its id
        /// is NOT the id of the session that wrote them.</summary>
        public TraceSession Session { get; internal set; }

        /// <summary>The boot stamp baked into the part names, local time.</summary>
        public DateTime StampLocal { get; internal set; }

        /// <summary>The highest part number seen, archived or not.</summary>
        public int HighestPart { get; internal set; }

        /// <summary>How many parts this call archived (skipping those already
        /// archived and those with a pending record of their own).</summary>
        public int Archived { get; internal set; }

        /// <summary>The file names of the parts, in part order.</summary>
        public IReadOnlyList<string> PartFiles { get; internal set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Boot-time adoption of rotated part files a previous run left behind.
    ///
    /// <para><b>What it no longer does.</b> Until Sprint 45 Track H7 the
    /// application grouped these parts, made a NEW <see cref="TraceSession"/>
    /// for each group, marked it <c>killed</c>, and — if a live trace file was
    /// also present — attached that file as the final part of whichever chain
    /// had the newest filename stamp. Astra's ruling on the pending-record
    /// failure found both halves wrong (implementation note 5): the absence of
    /// a pending record is not evidence that the session was killed, only
    /// that nothing recorded how it ended, and a live leftover joined to a
    /// chain "solely because that chain has the newest filename timestamp" is
    /// a guess dressed as identity. Since Track H6 a part with a FAILED
    /// pending-record write is exactly a sidecarless part, so the old adopter
    /// would have filed a session whose real outcome was
    /// <c>connection_dropped</c> under a fabricated id and the word
    /// <c>killed</c>.</para>
    ///
    /// <para><b>What it does now.</b> The parts are still filed — the bytes
    /// are evidence and the retention sweep must not delete them unread — but
    /// under an inventory identity the manifest marks <c>orphaned</c>, with
    /// outcome <c>unknown</c> and a detail saying why. A live leftover is
    /// joined to a chain only when the file itself says so: a rotated or
    /// checkpointed live file begins with a continuation header naming the
    /// part it continues from, and that name carries the chain's stamp. That
    /// is the file's own claim, not an inference from timestamps.</para>
    /// </summary>
    public static class TraceLeftoverAdoption
    {
        /// <summary>The detail written onto every orphaned entry.</summary>
        public const string OrphanDetail =
            "Leftover trace parts filed at the next launch under an inventory identity: nothing"
            + " recorded which session wrote them or how it ended (no durable record was found)";

        // "--- trace continues from part 001 (JJFlexRadioTrace-20260924-101500-part-001.txt) — this is part 002 ---"
        private static readonly Regex ContinuationHeader = new Regex(
            @"--- trace continues from part (?<from>\d{3}) \((?<name>[^)]+)\) .*? this is part (?<this>\d{3}) ---",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // <stem>-yyyyMMdd-HHmmss-part-NNN[-collisionSuffix].txt
        private static readonly Regex PartName = new Regex(
            @"^(?<stem>.+?)-(?<stamp>\d{8}-\d{6})-part-(?<part>\d{3})(?:-\d+)?\.txt$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Parse a rotation or checkpoint continuation header.
        /// </summary>
        /// <returns>True when <paramref name="line"/> is one, with the named
        /// predecessor's file name and the part numbers it states.</returns>
        public static bool TryParseContinuationHeader(string line, out string partFileName,
                                                      out int fromPart, out int thisPart)
        {
            partFileName = null;
            fromPart = 0;
            thisPart = 0;
            if (string.IsNullOrEmpty(line)) return false;
            Match m = ContinuationHeader.Match(line);
            if (!m.Success) return false;
            partFileName = m.Groups["name"].Value;
            fromPart = int.Parse(m.Groups["from"].Value, CultureInfo.InvariantCulture);
            thisPart = int.Parse(m.Groups["this"].Value, CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// Parse a part file's name into its stem, boot stamp and part number.
        /// </summary>
        public static bool TryParsePartName(string fileName, out string stem, out DateTime stampLocal, out int part)
        {
            stem = null;
            stampLocal = default;
            part = 0;
            if (string.IsNullOrEmpty(fileName)) return false;
            Match m = PartName.Match(fileName);
            if (!m.Success) return false;
            if (!DateTime.TryParseExact(m.Groups["stamp"].Value, "yyyyMMdd-HHmmss",
                                        CultureInfo.InvariantCulture, DateTimeStyles.None, out stampLocal))
            {
                return false;
            }
            stem = m.Groups["stem"].Value;
            part = int.Parse(m.Groups["part"].Value, CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// Archive every sidecarless, not-yet-archived part file under
        /// <paramref name="baseDir"/> whose stem is <paramref name="liveStem"/>,
        /// as orphaned evidence: outcome <c>unknown</c>, an inventory session
        /// id, and the manifest's <c>orphaned</c> flag. Parts with a pending
        /// record are left for <see cref="TraceArchiveWorker.RecoverPending"/>,
        /// which knows their real session.
        /// </summary>
        /// <returns>One adoption per boot stamp found, oldest first. Empty when
        /// there is nothing to adopt.</returns>
        public static IReadOnlyList<LeftoverChainAdoption> AdoptLeftoverParts(string baseDir,
                                                                             string liveStem,
                                                                             string archiveRootDir)
        {
            var result = new List<LeftoverChainAdoption>();
            if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(liveStem)) return result;
            try
            {
                if (!Directory.Exists(baseDir)) return result;
                var chains = new SortedDictionary<DateTime, List<(string Path, int Part)>>();

                foreach (string partFile in Directory.GetFiles(baseDir, liveStem + "-*-part-*.txt"))
                {
                    if (!TryParsePartName(Path.GetFileName(partFile), out string stem, out DateTime stamp, out int partNo)) continue;
                    if (!string.Equals(stem, liveStem, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!chains.TryGetValue(stamp, out var list))
                    {
                        list = new List<(string, int)>();
                        chains[stamp] = list;
                    }
                    list.Add((partFile, partNo));
                }

                foreach (var kvp in chains)
                {
                    DateTime stampLocal = kvp.Key;
                    var session = new TraceSession(stampLocal.ToUniversalTime());
                    session.MarkIdentityUnknown();
                    session.MarkOutcome(TraceSessionOutcome.Unknown, OrphanDetail);

                    int highest = 0;
                    int archived = 0;
                    var names = new List<string>();
                    foreach (var item in kvp.Value.OrderBy(t => t.Part))
                    {
                        if (item.Part > highest) highest = item.Part;
                        string fileName = Path.GetFileName(item.Path);
                        names.Add(fileName);
                        if (SessionArchive.IsSourceArchived(archiveRootDir, fileName)) continue;
                        // A part with a pending record belongs to a real session
                        // whose own archive is outstanding. Filing it here would
                        // put it under the inventory id instead of its own; the
                        // downstream dedup would stop a double archive, but the
                        // surviving entry would be the wrong one. Recovery picks
                        // these up in this same boot.
                        if (TraceArchiveWorker.IsPendingWork(item.Path)) continue;
                        string rel = SessionArchive.ArchiveSession(archiveRootDir, item.Path, session,
                            deleteSourceAfter: false, partNumber: item.Part, isFinalPart: false);
                        if (!string.IsNullOrEmpty(rel)) archived++;
                    }

                    result.Add(new LeftoverChainAdoption
                    {
                        Session = session,
                        StampLocal = stampLocal,
                        HighestPart = highest,
                        Archived = archived,
                        PartFiles = names,
                    });
                }
            }
            catch (Exception ex)
            {
                Tracing.ErrTraceOnly(ex);
            }
            if (result.Count > 0)
            {
                int total = 0;
                foreach (LeftoverChainAdoption a in result) total += a.Archived;
                TraceRecordingHealth.NoteOrphansAdopted(total);
            }
            return result;
        }

        /// <summary>
        /// Which adopted chain, if any, a live leftover file continues — by
        /// the file's OWN continuation header, never by timestamp proximity.
        /// </summary>
        /// <returns>The chain whose stamp the header's named part carries, or
        /// null: the live file is then a session of its own.</returns>
        public static LeftoverChainAdoption ChainForLiveLeftover(IReadOnlyList<LeftoverChainAdoption> chains,
                                                                 string livePath)
        {
            if (chains == null || chains.Count == 0 || string.IsNullOrEmpty(livePath)) return null;
            string header = ReadFirstHeaderLine(livePath);
            if (!TryParseContinuationHeader(header, out string named, out _, out _)) return null;
            if (!TryParsePartName(named, out _, out DateTime stamp, out _)) return null;
            foreach (LeftoverChainAdoption chain in chains)
            {
                if (chain.StampLocal == stamp) return chain;
            }
            return null;
        }

        /// <summary>
        /// The continuation header is the first line a rotated or checkpointed
        /// live file carries — after the trace prefix on a checkpointed one,
        /// bare on a rotated one. Read a few lines rather than exactly one, so
        /// a header preceded by nothing more than a session-opened line is
        /// still found; anything further in is not a header.
        /// </summary>
        private static string ReadFirstHeaderLine(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                for (int i = 0; i < 4; i++)
                {
                    string line = sr.ReadLine();
                    if (line == null) break;
                    if (line.Contains("--- trace continues from part", StringComparison.Ordinal)) return line;
                }
            }
            catch (Exception ex)
            {
                Tracing.ErrTraceOnly(ex);
            }
            return null;
        }
    }
}
