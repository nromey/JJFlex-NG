using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using JJTrace;

namespace Radios.Fixer
{
    /// <summary>How a recorded stage stands. Absence of a record means the
    /// stage was never attempted — there is deliberately no value for that,
    /// because a written-down "not run" with no reason would be exactly the
    /// blank the run rules forbid.</summary>
    public enum FixerStageStatus
    {
        /// <summary>The stage ran and produced an answer.</summary>
        Ran = 0,

        /// <summary>The operator skipped it, and said why.</summary>
        Skipped,

        /// <summary>It was attempted and could not run — the host supplied no
        /// executor, or the executor failed. Distinct from Ran because there
        /// is no measurement, and distinct from Skipped because nobody chose
        /// this.</summary>
        CouldNotRun,
    }

    /// <summary>One stage's recorded outcome within a run.</summary>
    public sealed class FixerStageResult
    {
        /// <summary>The run this belongs to. Stamped on every result so a
        /// result can never drift away from its run.</summary>
        public string RunId { get; }

        public string StageId { get; }

        /// <summary>When THIS result was recorded — its own timestamp, not the
        /// run's. Stages run out of order, and a report where one stage is
        /// forty minutes older than another must be able to say so.</summary>
        public DateTime AtUtc { get; }

        /// <summary>Position in the order things were actually done, shared
        /// with fix records so the report can interleave them truthfully.</summary>
        public int Sequence { get; }

        /// <summary>True when this replaced an earlier result for the same
        /// stage. The replacement is total — a stale result under a stage the
        /// operator just re-attempted is drift in miniature — but the fact of
        /// the re-run is kept and said.</summary>
        public bool WasReRun { get; }

        public FixerStageStatus Status { get; }

        /// <summary>The reason, when <see cref="Status"/> is Skipped.</summary>
        public FixerSkipChoice Skip { get; }

        public string Answer { get; }
        public IReadOnlyList<FixerFinding> Findings { get; }
        public string Evidence { get; }
        public object Payload { get; }

        internal FixerStageResult(string runId, string stageId, DateTime atUtc, int sequence,
                                  bool wasReRun, FixerStageStatus status, FixerSkipChoice skip,
                                  string answer, IReadOnlyList<FixerFinding> findings,
                                  string evidence, object payload)
        {
            RunId = runId ?? "";
            StageId = stageId ?? "";
            AtUtc = atUtc;
            Sequence = sequence;
            WasReRun = wasReRun;
            Status = status;
            Skip = skip;
            Answer = answer ?? "";
            Findings = findings ?? Array.Empty<FixerFinding>();
            Evidence = evidence ?? "";
            Payload = payload;
        }
    }

    /// <summary>One fix applied (or attempted) during a run. Every fix is
    /// recorded — the operator can undo it, later stages are read against a
    /// configuration that changed mid-run, and FlexRadio must not be shown
    /// measurements taken under a setup we quietly altered.</summary>
    public sealed class FixerFixRecord
    {
        public string RunId { get; }
        public string StageId { get; }
        public string FindingId { get; }
        public DateTime AtUtc { get; }

        /// <summary>Shares the run's sequence counter with stage results, so
        /// "which stages ran after this change" is arithmetic, not memory.</summary>
        public int Sequence { get; }

        public bool Succeeded { get; }

        /// <summary>What was wrong, taken from the finding at the moment the
        /// fix was pressed.</summary>
        public string WhatWasWrong { get; }

        /// <summary>What it became — or, on failure, why it could not be done.</summary>
        public string WhatItBecame { get; }

        internal FixerFixRecord(string runId, string stageId, string findingId, DateTime atUtc,
                                int sequence, bool succeeded, string whatWasWrong, string whatItBecame)
        {
            RunId = runId ?? "";
            StageId = stageId ?? "";
            FindingId = findingId ?? "";
            AtUtc = atUtc;
            Sequence = sequence;
            Succeeded = succeeded;
            WhatWasWrong = whatWasWrong ?? "";
            WhatItBecame = whatItBecame ?? "";
        }
    }

    /// <summary>
    /// One run of one stage set: the engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generic over the stage set — it runs whatever data it was given and
    /// records what happened. Domain knowledge lives in the stage set; radio
    /// and audio access live in the host's delegates; this class owns the run
    /// rules: one test ID stamped on everything, a timestamp on every result,
    /// skips recorded with their reason and never blank, re-runs replacing
    /// and saying so, and the actual order kept.
    /// </para>
    /// <para>
    /// <b>Nothing here transmits, ever.</b> A transmitting stage transmits
    /// inside the delegate the host bound to it; when no delegate was bound,
    /// the stage is recorded as unable to run and nothing else happens. That
    /// refusal is the engine's whole enforcement of the transmit boundary,
    /// and it is deliberately dull.
    /// </para>
    /// <para>
    /// Blocking, like the rest of ChainChecks' plumbing — call it off the UI
    /// thread. The clock and the ID's randomness are injectable so every time
    /// rule is testable at any spacing without waiting.
    /// </para>
    /// </remarks>
    public sealed class FixerRun
    {
        private readonly Func<DateTime> _clock;
        private readonly Dictionary<string, FixerStageResult> _results =
            new Dictionary<string, FixerStageResult>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FixerFixRecord> _fixes = new List<FixerFixRecord>();
        private int _sequence;

        public FixerStageSet Set { get; }

        /// <summary>The test ID. See <see cref="FixerRunId"/> for why it looks
        /// the way it does.</summary>
        public string RunId { get; }

        public DateTime StartedUtc { get; }

        public FixerRun(FixerStageSet set, Func<DateTime> clockUtc = null, Random rng = null)
        {
            Set = set ?? throw new ArgumentNullException(nameof(set));
            _clock = clockUtc ?? (() => DateTime.UtcNow);
            RunId = FixerRunId.New(rng ?? Random.Shared);
            StartedUtc = _clock();

            Tracing.TraceLine("FixerRun " + RunId + ": started on stage set '" + set.Id + "'",
                              TraceLevel.Info);
        }

        /// <summary>The report's "generated at" stamp comes from the same
        /// clock as every result, so tests can hold the whole timeline.</summary>
        public DateTime NowUtc => _clock();

        /// <summary>This stage's recorded result, or null when it has never
        /// been attempted.</summary>
        public FixerStageResult ResultFor(string stageId)
            => _results.TryGetValue(stageId ?? "", out FixerStageResult r) ? r : null;

        /// <summary>Recorded results in the order they actually happened —
        /// which the report states, because it is usually not the order the
        /// stages are listed in.</summary>
        public IReadOnlyList<FixerStageResult> ResultsInRunOrder
            => _results.Values.OrderBy(r => r.Sequence).ToArray();

        /// <summary>Every fix applied or attempted, in order.</summary>
        public IReadOnlyList<FixerFixRecord> FixesApplied => _fixes;

        /// <summary>
        /// Run a stage now. Replaces any earlier result for the stage and
        /// marks the replacement. Never throws for a host that wired nothing
        /// or an executor that failed — both are recorded as what they are,
        /// because a diagnostic that dies mid-diagnosis loses the whole run.
        /// </summary>
        public FixerStageResult RunStage(string stageId, CancellationToken cancel = default)
        {
            FixerStage stage = RequireStage(stageId);

            FixerOutcome outcome;
            FixerStageStatus status;

            if (stage.Execute == null)
            {
                // The honest refusal. For a transmitting stage this is the
                // transmit boundary holding: no delegate, no key-down, and a
                // record saying so rather than a gap.
                status = FixerStageStatus.CouldNotRun;
                outcome = new FixerOutcome
                {
                    Answer = "This check could not run: the application did not supply the "
                           + "code that performs it. Nothing was measured"
                           + (stage.Transmits ? " and nothing was transmitted" : "") + ".",
                };
                Tracing.TraceLine("FixerRun " + RunId + ": stage '" + stage.Id
                    + "' has no executor bound — recorded as could-not-run", TraceLevel.Warning);
            }
            else
            {
                try
                {
                    var context = new FixerStageContext(RunId, stage, cancel, ResultFor);
                    outcome = stage.Execute(context) ?? new FixerOutcome
                    {
                        Answer = "The check ran and reported nothing at all, which is itself "
                               + "worth knowing — there is no measurement here.",
                    };
                    status = FixerStageStatus.Ran;
                }
                catch (Exception ex)
                {
                    status = FixerStageStatus.CouldNotRun;
                    outcome = new FixerOutcome
                    {
                        Answer = "This check failed part-way and produced no result: " + ex.Message,
                        Evidence = ex.ToString(),
                    };
                    Tracing.TraceLine("FixerRun " + RunId + ": stage '" + stage.Id
                        + "' threw — " + ex.Message, TraceLevel.Error);
                }
            }

            return Record(stage, status, null, outcome);
        }

        /// <summary>
        /// Skip a stage for one of its declared reasons. Recorded, never
        /// blank: "not run, and why" is evidence, and the two reasons a
        /// microphone stage offers do very different things to the report.
        /// </summary>
        public FixerStageResult SkipStage(string stageId, string skipChoiceId)
        {
            FixerStage stage = RequireStage(stageId);

            FixerSkipChoice choice = stage.FindSkip(skipChoiceId);
            if (choice == null)
                throw new ArgumentException("stage '" + stage.Id + "' has no skip choice '"
                    + skipChoiceId + "'", nameof(skipChoiceId));

            var outcome = new FixerOutcome
            {
                Answer = "Not run. The reason given: \"" + choice.Label + "\" " + choice.EffectText,
            };
            return Record(stage, FixerStageStatus.Skipped, choice, outcome);
        }

        /// <summary>
        /// Apply the fix a finding offered. Invokes the host-bound action and
        /// records what was wrong, what it became and when — success or
        /// failure, because both readers of the report need to know the
        /// configuration changed mid-run (or that a change was attempted).
        /// </summary>
        public FixerFixRecord ApplyFix(string stageId, string findingId)
        {
            FixerStageResult result = ResultFor(stageId)
                ?? throw new ArgumentException("stage '" + stageId + "' has no result to fix "
                    + "anything from", nameof(stageId));

            FixerFinding finding = result.Findings.FirstOrDefault(
                f => string.Equals(f.Id, findingId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("stage '" + stageId + "' has no finding '"
                    + findingId + "'", nameof(findingId));

            if (finding.Owner != FixOwner.Us)
                throw new InvalidOperationException("finding '" + findingId + "' is not one "
                    + "JJ Flexible can fix — a fix control should never have been offered for it");

            FixerFixOutcome applied;
            if (Set.FixActions.TryGetValue(finding.FixActionId, out FixerFixAction action)
                && action != null)
            {
                try { applied = action(); }
                catch (Exception ex)
                {
                    applied = FixerFixOutcome.Failed("the fix failed part-way: " + ex.Message);
                }
            }
            else
            {
                // A button the host never wired. Recorded honestly rather than
                // thrown or ignored: "nothing changed, and here is why" is the
                // one outcome that must never be silent.
                applied = FixerFixOutcome.Failed("the application did not supply the code "
                    + "that performs this fix, so nothing was changed");
            }

            var record = new FixerFixRecord(RunId, stageId, finding.Id, _clock(), ++_sequence,
                                            applied.Succeeded, finding.WhatIsWrong,
                                            applied.WhatItBecame);
            _fixes.Add(record);

            Tracing.TraceLine("FixerRun " + RunId + ": fix '" + finding.FixActionId + "' on "
                + "stage '" + stageId + "' " + (applied.Succeeded ? "applied — " : "FAILED — ")
                + applied.WhatItBecame, applied.Succeeded ? TraceLevel.Info : TraceLevel.Warning);

            return record;
        }

        /// <summary>Stage results recorded after this fix — the ones measured
        /// against the changed configuration.</summary>
        public IReadOnlyList<FixerStageResult> ResultsAfter(FixerFixRecord fix)
            => _results.Values.Where(r => fix != null && r.Sequence > fix.Sequence)
                              .OrderBy(r => r.Sequence).ToArray();

        // -------- plumbing --------

        private FixerStage RequireStage(string stageId)
            => Set.Find(stageId)
               ?? throw new ArgumentException("stage set '" + Set.Id + "' has no stage '"
                   + stageId + "'", nameof(stageId));

        private FixerStageResult Record(FixerStage stage, FixerStageStatus status,
                                        FixerSkipChoice skip, FixerOutcome outcome)
        {
            bool rerun = _results.ContainsKey(stage.Id);
            var result = new FixerStageResult(RunId, stage.Id, _clock(), ++_sequence, rerun,
                                              status, skip, outcome.Answer, outcome.Findings,
                                              outcome.Evidence, outcome.Payload);
            _results[stage.Id] = result;

            Tracing.TraceLine("FixerRun " + RunId + ": stage '" + stage.Id + "' recorded as "
                + status + (rerun ? " (re-run)" : ""), TraceLevel.Info);
            return result;
        }
    }
}
