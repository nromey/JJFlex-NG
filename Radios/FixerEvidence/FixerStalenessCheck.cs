using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Radios.Fixer.Evidence
{
    /// <summary>How one stage's measurement stands against the settings the
    /// radio and audio system hold right now.</summary>
    public enum FixerStageFreshness
    {
        /// <summary>No measurement to check: never attempted, skipped, or
        /// could not run. Nothing here can go stale.</summary>
        NoMeasurement = 0,

        /// <summary>Everything the stage declared still holds its recorded
        /// value. The measurement still describes this radio.</summary>
        Fresh,

        /// <summary>At least one declared setting has changed since the stage
        /// ran. The measurement no longer describes this radio.</summary>
        Stale,

        /// <summary>Nothing is known to have changed, but at least one
        /// declared setting cannot be compared right now — typically because
        /// no radio is connected. Named, never glossed as fresh.</summary>
        CannotVerify,
    }

    /// <summary>One stage's verdict, with every difference named.</summary>
    public sealed class FixerStageStaleness
    {
        public string StageId { get; }
        public int Number { get; }
        public string Title { get; }
        public FixerStageFreshness State { get; }

        /// <summary>Each change as a sentence: "Tune power changed from 10
        /// watts to 100 watts." Empty unless <see cref="State"/> is Stale.</summary>
        public IReadOnlyList<string> Changes { get; }

        /// <summary>Each comparison that could not be made, as a sentence.</summary>
        public IReadOnlyList<string> CannotCompare { get; }

        /// <summary>The one-sentence verdict for this stage, or empty when
        /// there is nothing to say (fresh, or no measurement).</summary>
        public string Verdict { get; }

        internal FixerStageStaleness(string stageId, int number, string title,
                                     FixerStageFreshness state,
                                     IReadOnlyList<string> changes,
                                     IReadOnlyList<string> cannotCompare,
                                     string verdict)
        {
            StageId = stageId ?? "";
            Number = number;
            Title = title ?? "";
            State = state;
            Changes = changes ?? Array.Empty<string>();
            CannotCompare = cannotCompare ?? Array.Empty<string>();
            Verdict = verdict ?? "";
        }
    }

    /// <summary>The whole run's staleness picture, with prose ready to show
    /// or speak.</summary>
    public sealed class FixerStalenessReport
    {
        public IReadOnlyList<FixerStageStaleness> Stages { get; }

        public bool AnythingStale => Stages.Any(s => s.State == FixerStageFreshness.Stale);
        public bool AnythingUnverifiable => Stages.Any(s => s.State == FixerStageFreshness.CannotVerify);

        /// <summary>The lowest-numbered stale stage — where "run again from
        /// stage N" points. Null when nothing is stale.</summary>
        public FixerStageStaleness EarliestStale
            => Stages.Where(s => s.State == FixerStageFreshness.Stale)
                     .OrderBy(s => s.Number).FirstOrDefault();

        internal FixerStalenessReport(IReadOnlyList<FixerStageStaleness> stages)
        {
            Stages = stages ?? Array.Empty<FixerStageStaleness>();
        }

        /// <summary>
        /// The picture in prose: each distinct change named once, then where
        /// to resume from. The specific fact, not a generic "settings have
        /// changed" banner — the specific fact is the entire point (#252).
        /// </summary>
        public string Summary()
        {
            if (AnythingStale)
            {
                // A change that stales three stages is one change; say it once.
                var distinct = new List<string>();
                foreach (string sentence in Stages.SelectMany(s => s.Changes))
                    if (!distinct.Contains(sentence)) distinct.Add(sentence);

                FixerStageStaleness from = EarliestStale;
                var stale = Stages.Where(s => s.State == FixerStageFreshness.Stale)
                                  .OrderBy(s => s.Number).ToList();
                string which = stale.Count == 1
                    ? "Stage " + Describe(stale[0]) + " was measured before this change and no "
                      + "longer describes this radio."
                    : "Stages " + string.Join(", ", stale.Select(Describe)) + " were measured "
                      + "before this change and no longer describe this radio.";

                return string.Join(" ", distinct) + " " + which
                     + " Run again from stage " + Describe(from) + ".";
            }

            if (AnythingUnverifiable)
            {
                var reasons = new List<string>();
                foreach (string sentence in Stages.SelectMany(s => s.CannotCompare))
                    if (!reasons.Contains(sentence)) reasons.Add(sentence);
                return "Whether these results still hold cannot be fully checked right now. "
                     + string.Join(" ", reasons);
            }

            if (Stages.All(s => s.State == FixerStageFreshness.NoMeasurement))
                return "No stage has a measurement to check.";

            return "Nothing these stages depended on has changed since they ran.";
        }

        private static string Describe(FixerStageStaleness s)
            => s.Number.ToString(CultureInfo.InvariantCulture)
             + (s.Title.Length > 0 ? " (" + s.Title + ")" : "");
    }

    /// <summary>
    /// Invalidation as arithmetic: compare each stage's recorded fingerprint
    /// against the values the settings hold now, and let every difference
    /// name itself.
    /// </summary>
    /// <remarks>
    /// Used in two places with one meaning: on a stopped run before resuming
    /// (which stages survived the interruption), and inside a live run after
    /// a fix (which earlier stages the change just invalidated). Same
    /// comparison, same sentences, one home.
    /// </remarks>
    public static class FixerStalenessCheck
    {
        public static FixerStalenessReport Check(FixerRunRecord record,
                                                 FixerSettingProbeSet current)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (current == null) throw new ArgumentNullException(nameof(current));

            IReadOnlyDictionary<string, RecordedStage> latest = record.LatestResultsPerStage();
            var stages = new List<FixerStageStaleness>();

            foreach (RecordedStageInfo info in record.Stages.OrderBy(s => s.Number))
            {
                latest.TryGetValue(info.Id ?? "", out RecordedStage result);
                stages.Add(CheckStage(info, result, current));
            }
            return new FixerStalenessReport(stages);
        }

        private static FixerStageStaleness CheckStage(RecordedStageInfo info,
                                                      RecordedStage result,
                                                      FixerSettingProbeSet current)
        {
            // Only an actual measurement can go stale. A skip is a recorded
            // decision and a could-not-run is a recorded refusal; neither
            // claims anything about the radio that a setting change could
            // falsify.
            if (result == null
                || !string.Equals(result.Status, FixerStageStatus.Ran.ToString(),
                                  StringComparison.OrdinalIgnoreCase))
            {
                return new FixerStageStaleness(info.Id, info.Number, info.Title,
                    FixerStageFreshness.NoMeasurement,
                    Array.Empty<string>(), Array.Empty<string>(), "");
            }

            var changes = new List<string>();
            var cannot = new List<string>();

            foreach (RecordedSetting stored in result.Settings)
            {
                if (string.IsNullOrEmpty(stored.Value))
                {
                    cannot.Add(stored.Name + " could not be read when this stage ran, so it "
                             + "cannot be compared.");
                    continue;
                }

                RecordedSetting now = current.ReadCurrent(stored.Key);
                if (now == null || string.IsNullOrEmpty(now.Value))
                {
                    cannot.Add(stored.Name + " cannot be checked right now; it was "
                             + stored.Value + " when this stage ran.");
                    continue;
                }

                if (!string.Equals(stored.Value, now.Value, StringComparison.Ordinal))
                {
                    changes.Add(stored.Name + " changed from " + stored.Value + " to "
                              + now.Value + ".");
                }
            }

            // An older record (or a stage that declared dependencies after
            // this run was written) has nothing stored to compare. Declared
            // dependencies with no stored values cannot be verified.
            if (result.Settings.Count == 0 && current.DeclaredFor(info.Id).Count > 0)
            {
                cannot.Add("No settings were recorded for stage "
                         + info.Number.ToString(CultureInfo.InvariantCulture)
                         + ", so its result cannot be checked against the current setup.");
            }

            if (changes.Count > 0)
            {
                string verdict = "Stage " + info.Number.ToString(CultureInfo.InvariantCulture)
                    + (info.Title.Length > 0 ? " (" + info.Title + ")" : "")
                    + " was measured before this change and no longer describes this radio. "
                    + "Run it again.";
                return new FixerStageStaleness(info.Id, info.Number, info.Title,
                    FixerStageFreshness.Stale, changes, cannot, verdict);
            }

            if (cannot.Count > 0)
            {
                string verdict = "Whether stage "
                    + info.Number.ToString(CultureInfo.InvariantCulture)
                    + (info.Title.Length > 0 ? " (" + info.Title + ")" : "")
                    + " still holds cannot be fully checked right now.";
                return new FixerStageStaleness(info.Id, info.Number, info.Title,
                    FixerStageFreshness.CannotVerify, changes, cannot, verdict);
            }

            return new FixerStageStaleness(info.Id, info.Number, info.Title,
                FixerStageFreshness.Fresh, changes, cannot, "");
        }
    }
}
