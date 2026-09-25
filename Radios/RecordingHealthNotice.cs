using System;
using System.Collections.Generic;
using System.Globalization;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The operator's sentences about recording health, composed from a
    /// <see cref="TraceRecordingHealthSnapshot"/> away from any window so they
    /// can be read and tested as prose. Sprint 45 Track H7, under Astra's
    /// ruling on the pending-record failure.
    ///
    /// <para><b>Every string is lexicon, and every string is a DRAFT.</b>
    /// Keys under <c>logging.recording.health</c> in
    /// <c>Radios/Lexicon/logging.json</c>, listed with what each must be able
    /// to say truthfully in the Track H7 wording file for Noel. Nothing here
    /// claims more than the facts allow: a path permits offering a file, not
    /// claiming a complete recording; a queued job is pending work, not a
    /// saved archive.</para>
    ///
    /// <para><b>Three readers, one composer.</b> The Diagnostics tab's status
    /// sentence (<see cref="StatusSentence"/>), the support snapshot that
    /// travels in the problem-report bundle and the About page
    /// (<see cref="SnapshotLines"/>), and the announcement the health watch
    /// makes when something changes (<see cref="Announcement"/>). The three
    /// cannot disagree because they read one state through one composer.</para>
    /// </summary>
    public static class RecordingHealthNotice
    {
        /// <summary>
        /// The short clause spoken once, and the detail the Problems list
        /// keeps, for one change. Null for a change the operator is not told
        /// about (a condition resolving, a healthy sink opening).
        /// </summary>
        public static (FailureKind Kind, string What, string Detail)? Announcement(TraceRecordingHealthChange change)
        {
            if (change == null) return null;
            switch (change.Kind)
            {
                case TraceRecordingHealthChangeKind.ConditionRaised:
                    {
                        TraceRecoveryCondition c = change.Condition;
                        if (c == null) return null;
                        return (FailureKind.RecordingRecoveryAtRisk, ConditionWhat(c), ConditionDetail(c));
                    }
                case TraceRecordingHealthChangeKind.SinkFailed:
                    {
                        TraceRecordingHealthSnapshot s = change.Snapshot;
                        return (FailureKind.RecordingStopped,
                            Lexicon.Get("logging.recording.health.sink_failed_what"),
                            Lexicon.Get("logging.recording.health.sink_failed_detail",
                                ("path", string.IsNullOrEmpty(s?.SinkPath) ? Lexicon.Get("logging.recording.health.unknown_path") : s.SinkPath),
                                ("fault", s?.SinkFault ?? Lexicon.Get("logging.recording.health.unknown_fault"))));
                    }
                default:
                    return null;
            }
        }

        /// <summary>
        /// Which of the three things is wrong with a ticket decides which
        /// sentences describe it, and the three are kept apart on purpose: an
        /// archive that would not commit, an index file that would not write,
        /// and a tail that may be short are different consequences with
        /// different next steps. Track H7 folded the third into the second, so
        /// a ticket whose only problem was its tail was told its index file
        /// had not been written and that it would not be filed automatically
        /// — both false (the same conflation Sol's review of H7 found in the
        /// drop window, finding 3). Precedence: the archive failing is the
        /// most consequential and is said first; the index file next; the tail
        /// only when it is the whole story. Every key below is a literal in
        /// its own call, because the lexicon coverage test reads them from the
        /// source and a key built from parts is a key it cannot verify.
        /// </summary>
        private static bool TailIsTheWholeStory(TraceRecoveryCondition c)
            => c.ArchiveFailureStage == null && c.RecoveryRecordWritten && c.TailUncertain;

        /// <summary>The short clause spoken once for one unresolved ticket.</summary>
        public static string ConditionWhat(TraceRecoveryCondition c)
        {
            if (c == null) return string.Empty;
            return Lexicon.Get(c.ArchiveFailureStage != null
                ? "logging.recording.health.archive_failed_what"
                : TailIsTheWholeStory(c)
                    ? "logging.recording.health.tail_uncertain_what"
                    : "logging.recording.health.record_failed_what");
        }

        /// <summary>The consequence and next step for one unresolved ticket.</summary>
        public static string ConditionDetail(TraceRecoveryCondition c)
        {
            if (c == null) return string.Empty;
            string path = string.IsNullOrEmpty(c.RawPath)
                ? Lexicon.Get("logging.recording.health.unknown_path")
                : c.RawPath;
            string why = ConditionSummary(c);
            if (c.ArchiveFailureStage != null)
            {
                return Lexicon.Get("logging.recording.health.archive_failed_detail",
                    ("path", path), ("why", why),
                    ("retained", c.RawRetained
                        ? Lexicon.Get("logging.recording.health.raw_present")
                        : Lexicon.Get("logging.recording.health.raw_missing")));
            }
            if (TailIsTheWholeStory(c))
            {
                return Lexicon.Get("logging.recording.health.tail_uncertain_detail",
                    ("path", path), ("why", why));
            }
            return Lexicon.Get("logging.recording.health.record_failed_detail",
                ("path", path), ("why", why),
                ("days", SessionArchive.DefaultRetentionDays.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>One clause naming what is wrong with a ticket — joined
        /// with "and" when more than one thing is.</summary>
        public static string ConditionSummary(TraceRecoveryCondition c)
        {
            var parts = new List<string>();
            if (c == null) return string.Empty;
            if (!c.RecoveryRecordWritten)
                parts.Add(Lexicon.Get("logging.recording.health.condition_record_missing"));
            if (c.ArchiveFailureStage != null)
                parts.Add(Lexicon.Get("logging.recording.health.condition_archive_failed",
                    ("stage", c.ArchiveFailureStage)));
            if (c.TailUncertain)
                parts.Add(Lexicon.Get("logging.recording.health.condition_tail_uncertain"));
            if (parts.Count == 0) return Lexicon.Get("logging.recording.health.condition_unspecified");
            return string.Join(Lexicon.Get("logging.recording.health.condition_join"), parts);
        }

        /// <summary>
        /// What to append to the Diagnostics tab's status sentence — empty
        /// when there is nothing to say, so the ordinary sentence is untouched.
        /// Leads with a space so it can be concatenated as a following sentence.
        /// </summary>
        public static string StatusSentence(TraceRecordingHealthSnapshot s)
        {
            if (s == null || !s.NeedsAttention) return string.Empty;
            string text = string.Empty;
            if (s.SinkState == TraceSinkState.Failed)
            {
                text += " " + Lexicon.Get("logging.recording.health.status_sink_failed",
                    ("fault", s.SinkFault ?? Lexicon.Get("logging.recording.health.unknown_fault")));
            }
            int n = s.Unresolved.Count;
            if (n == 1)
            {
                text += " " + Lexicon.Get("logging.recording.health.status_unresolved_one",
                    ("summary", ConditionSummary(s.Unresolved[0])),
                    ("path", s.Unresolved[0].RawPath ?? Lexicon.Get("logging.recording.health.unknown_path")));
            }
            else if (n > 1)
            {
                text += " " + Lexicon.Get("logging.recording.health.status_unresolved_many",
                    ("count", n.ToString(CultureInfo.InvariantCulture)));
            }
            return text;
        }

        /// <summary>
        /// The support-snapshot lines: one for the sink, one per unresolved
        /// ticket, one for history. Plain text for a file a person or a
        /// support script reads.
        /// </summary>
        public static IReadOnlyList<(string Label, string Value)> SnapshotLines(TraceRecordingHealthSnapshot s)
        {
            var lines = new List<(string, string)>();
            if (s == null) return lines;

            string sink;
            switch (s.SinkState)
            {
                case TraceSinkState.Recording:
                    sink = Lexicon.Get("logging.recording.health.snapshot_sink_recording");
                    break;
                case TraceSinkState.Failed:
                    sink = Lexicon.Get("logging.recording.health.snapshot_sink_failed",
                        ("fault", s.SinkFault ?? Lexicon.Get("logging.recording.health.unknown_fault")),
                        ("path", s.SinkPath ?? Lexicon.Get("logging.recording.health.unknown_path")));
                    break;
                default:
                    sink = Lexicon.Get("logging.recording.health.snapshot_sink_off");
                    break;
            }
            lines.Add((Lexicon.Get("logging.recording.health.snapshot_label_sink"), sink));

            if (s.Unresolved.Count == 0)
            {
                lines.Add((Lexicon.Get("logging.recording.health.snapshot_label_recovery"),
                           Lexicon.Get("logging.recording.health.snapshot_ok")));
            }
            else
            {
                foreach (TraceRecoveryCondition c in s.Unresolved)
                {
                    lines.Add((Lexicon.Get("logging.recording.health.snapshot_label_recovery"),
                        Lexicon.Get("logging.recording.health.snapshot_condition",
                            ("session", c.SessionId.ToString()),
                            ("part", c.PartNumber > 0 ? c.PartNumber.ToString("D3", CultureInfo.InvariantCulture)
                                                      : Lexicon.Get("logging.recording.health.whole_session")),
                            ("path", c.RawPath ?? Lexicon.Get("logging.recording.health.unknown_path")),
                            ("summary", ConditionSummary(c)),
                            ("retained", c.RawRetained
                                ? Lexicon.Get("logging.recording.health.raw_present")
                                : Lexicon.Get("logging.recording.health.raw_missing")))));
                }
            }

            if (s.HistoricalFailures > 0)
            {
                lines.Add((Lexicon.Get("logging.recording.health.snapshot_label_history"),
                    Lexicon.Get("logging.recording.health.snapshot_history",
                        ("count", s.HistoricalFailures.ToString(CultureInfo.InvariantCulture)),
                        ("last", s.LastFailure ?? string.Empty))));
            }
            if (s.OrphansAdoptedAtBoot > 0)
            {
                lines.Add((Lexicon.Get("logging.recording.health.snapshot_label_orphans"),
                    Lexicon.Get("logging.recording.health.orphans_adopted",
                        ("count", s.OrphansAdoptedAtBoot.ToString(CultureInfo.InvariantCulture)))));
            }
            return lines;
        }
    }

    /// <summary>
    /// Carries a recording-health change onto the operator's existing failure
    /// surface: <see cref="OperationFailure"/>, which the application records in
    /// its Problems list (Ctrl+J, Ctrl+R), counts on the Diagnostics tab, and
    /// announces once per kind with an earcon — queued behind whatever is
    /// speaking, never interrupting, never opening a window, never taking
    /// focus, and never while transmitting (all of that is the offer's own
    /// standing policy).
    ///
    /// <para><b>Why the existing route.</b> Astra: "For an unsolicited fault
    /// during transmit, do not steal keyboard focus with a new modal dialog.
    /// Carry the condition on the existing accessible surface and notification
    /// route. Across a window transition, the arriving surface must carry it;
    /// speech immediately before a window change is not reliable delivery."
    /// The Problems list IS the arriving surface: a missed announcement costs
    /// nothing, because the entry is there to be read afterwards.</para>
    ///
    /// <para><b>Deduplicated twice.</b> The health model raises a change only
    /// when a condition is raised or the sink's state moves; this watch then
    /// reports each ticket's condition at most once, whatever the model says
    /// later about the same ticket.</para>
    /// </summary>
    public static class RecordingHealthWatch
    {
        private static readonly object _gate = new object();
        private static readonly HashSet<Guid> _reportedTickets = new HashSet<Guid>();
        private static bool _installed;

        /// <summary>Wire the watch. Idempotent.</summary>
        public static void Install()
        {
            lock (_gate)
            {
                if (_installed) return;
                _installed = true;
            }
            TraceRecordingHealth.Changed += OnChanged;
        }

        /// <summary>Tests only: forget what has been reported.</summary>
        public static void ResetForTests()
        {
            lock (_gate) { _reportedTickets.Clear(); }
        }

        private static void OnChanged(TraceRecordingHealthChange change)
        {
            try
            {
                var announcement = RecordingHealthNotice.Announcement(change);
                if (announcement == null) return;

                if (change.Kind == TraceRecordingHealthChangeKind.ConditionRaised && change.Condition != null)
                {
                    lock (_gate)
                    {
                        if (!_reportedTickets.Add(change.Condition.TicketId)) return;
                    }
                }

                OperationFailure.Report(announcement.Value.Kind, announcement.Value.What, announcement.Value.Detail);
            }
            catch
            {
                // The report path must never fail the recording path.
            }
        }
    }
}
