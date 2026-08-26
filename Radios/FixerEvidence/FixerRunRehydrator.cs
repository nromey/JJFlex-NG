using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JJTrace;

namespace Radios.Fixer.Evidence
{
    /// <summary>Everything a resumed engine needs from a saved run, in the
    /// engine's own types.</summary>
    public sealed class FixerRehydratedState
    {
        public string RunId { get; }
        public DateTime StartedUtc { get; }

        /// <summary>Every result the record holds, in sequence order —
        /// including superseded ones, so a resumed run replays history in the
        /// order it happened and ends holding the same latest-per-stage view
        /// the original held.</summary>
        public IReadOnlyList<FixerStageResult> Results { get; }

        public IReadOnlyList<FixerFixRecord> Fixes { get; }

        /// <summary>The highest sequence number in the record. The engine's
        /// counter must resume ABOVE this, or new recordings would interleave
        /// falsely with old ones.</summary>
        public int MaxSequence { get; }

        internal FixerRehydratedState(string runId, DateTime startedUtc,
                                      IReadOnlyList<FixerStageResult> results,
                                      IReadOnlyList<FixerFixRecord> fixes)
        {
            RunId = runId;
            StartedUtc = startedUtc;
            Results = results;
            Fixes = fixes;
            MaxSequence = Math.Max(
                results.Count == 0 ? 0 : results.Max(r => r.Sequence),
                fixes.Count == 0 ? 0 : fixes.Max(f => f.Sequence));
        }
    }

    /// <summary>
    /// Turns a saved record back into the engine's own result and fix
    /// objects, ready for a resume (#252 part 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is HALF of resume — the half that owns the data. The other half
    /// is an engine constructor that accepts prior state
    /// (<c>FixerRun</c> is being restructured by another track this sprint,
    /// so that seam is specified in this track's report rather than built
    /// here). Until it lands, rehydrated state feeds the viewer and the
    /// staleness check, both of which work from the record alone.
    /// </para>
    /// <para>
    /// Malformed entries are skipped with a trace, never thrown on: a record
    /// written by a newer build with one field this build cannot read should
    /// cost that entry, not the whole run.
    /// </para>
    /// </remarks>
    public static class FixerRunRehydrator
    {
        public static FixerRehydratedState Rehydrate(FixerRunRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            var results = new List<FixerStageResult>();
            foreach (RecordedStage r in record.Results.OrderBy(r => r.Sequence))
            {
                FixerStageResult result = ToResult(record.RunId, r);
                if (result != null) results.Add(result);
            }

            var fixes = new List<FixerFixRecord>();
            foreach (RecordedFix f in record.Fixes.OrderBy(f => f.Sequence))
            {
                fixes.Add(new FixerFixRecord(record.RunId, f.StageId, f.FindingId, f.AtUtc,
                                             f.Sequence, f.Succeeded, f.WhatWasWrong,
                                             f.WhatItBecame));
            }

            return new FixerRehydratedState(record.RunId, record.StartedUtc, results, fixes);
        }

        private static FixerStageResult ToResult(string runId, RecordedStage r)
        {
            try
            {
                if (!Enum.TryParse(r.Status, ignoreCase: true, out FixerStageStatus status))
                {
                    Tracing.TraceLine("FixerRunRehydrator: stage '" + r.StageId
                        + "' has unknown status '" + r.Status + "' — skipped",
                        TraceLevel.Warning);
                    return null;
                }

                FixerSkipChoice skip = null;
                if (status == FixerStageStatus.Skipped && r.SkipChoiceId.Length > 0)
                {
                    Enum.TryParse(r.SkipEffect, ignoreCase: true, out FixerSkipEffect effect);
                    skip = new FixerSkipChoice(r.SkipChoiceId,
                        r.SkipLabel.Length > 0 ? r.SkipLabel : r.SkipChoiceId,
                        effect,
                        r.SkipEffectText.Length > 0 ? r.SkipEffectText : "(reason not recorded)");
                }

                var findings = new List<FixerFinding>();
                foreach (RecordedFinding f in r.Findings)
                {
                    try
                    {
                        Enum.TryParse(f.Owner, ignoreCase: true, out FixOwner owner);
                        findings.Add(new FixerFinding(f.Id, owner, f.WhatIsWrong, f.WhatToDo,
                            f.FixActionId.Length > 0 ? f.FixActionId : null, f.Critical));
                    }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine("FixerRunRehydrator: finding '" + f.Id
                            + "' could not be rebuilt — " + ex.Message, TraceLevel.Warning);
                    }
                }

                object payload = FixerPayloadCodec.Decode(r.PayloadType, r.PayloadJson);

                return new FixerStageResult(runId, r.StageId, r.AtUtc, r.Sequence, r.WasReRun,
                                            status, skip, r.Answer, findings, r.Evidence,
                                            payload);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerRunRehydrator: stage '" + r.StageId
                    + "' could not be rebuilt — " + ex.Message, TraceLevel.Warning);
                return null;
            }
        }
    }
}
