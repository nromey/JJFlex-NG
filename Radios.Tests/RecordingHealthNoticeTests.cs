using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JJTrace;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// What the operator is told about recording health — as ASSEMBLED
    /// sentences off the retained state — and that a change reaches the
    /// existing failure route exactly once per condition. Sprint 45 Track H7.
    /// </summary>
    /// <remarks>
    /// <para><b>MARKED FOR NOEL'S PROSE REVIEW.</b> Every string here is a
    /// draft an agent wrote; the wording file lists each with what it must be
    /// able to say truthfully. The assertions are about PROPERTIES of the
    /// sentences — that a healthy state adds nothing, that a condition names
    /// its file, that no key leaks — so a rewording does not turn this red for
    /// the wrong reason.</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RecordingHealthNoticeTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        public RecordingHealthNoticeTests(ITestOutputHelper output)
        {
            _out = output;
            TraceRecordingHealth.ResetForTests();
            RecordingHealthWatch.ResetForTests();
        }

        public void Dispose()
        {
            TraceRecordingHealth.ResetForTests();
            RecordingHealthWatch.ResetForTests();
        }

        private static TraceArchiveTicket TicketAt(string path, bool recordWritten, bool tailUncertain = false) =>
            new TraceArchiveTicket
            {
                SessionId = Guid.NewGuid(),
                PartNumber = 0,
                SourcePath = path,
                PendingRecordWritten = recordWritten,
                TailUncertain = tailUncertain,
                SinkFault = tailUncertain ? "disk full" : null,
            };

        private static void Invoke(string name, params object[] args) =>
            typeof(TraceRecordingHealth)
                .GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, args);

        [Fact]
        public void A_healthy_state_adds_nothing_to_the_status_sentence()
        {
            Invoke("NoteSink", TraceSinkState.Recording, null, @"C:\t\JJFlexRadioTrace.txt", Guid.NewGuid(), 1L);
            TraceRecordingHealthSnapshot s = TraceRecordingHealth.Snapshot();
            Assert.False(s.NeedsAttention);
            Assert.Equal(string.Empty, RecordingHealthNotice.StatusSentence(s));
        }

        [Fact]
        public void A_failed_record_is_a_status_sentence_that_names_the_file_and_what_is_missing()
        {
            string raw = Path.Combine(Path.GetTempPath(), "JJFlexRadioTrace-20260924-101500.txt");
            File.WriteAllText(raw, "x");
            try
            {
                Invoke("NoteDetached", TicketAt(raw, recordWritten: false));
                string sentence = RecordingHealthNotice.StatusSentence(TraceRecordingHealth.Snapshot());
                _out.WriteLine(sentence);
                Assert.StartsWith(" ", sentence, StringComparison.Ordinal);   // a following sentence
                Assert.Contains(raw, sentence, StringComparison.Ordinal);
                Assert.Contains("recovery index", sentence, StringComparison.Ordinal);
                Assert.DoesNotContain("logging.recording", sentence, StringComparison.Ordinal);
                Assert.EndsWith(".", sentence.TrimEnd(), StringComparison.Ordinal);
            }
            finally { try { File.Delete(raw); } catch { } }
        }

        [Fact]
        public void Two_conditions_are_counted_rather_than_read_out_in_full()
        {
            Invoke("NoteDetached", TicketAt(@"C:\t\a.txt", recordWritten: false));
            Invoke("NoteDetached", TicketAt(@"C:\t\b.txt", recordWritten: false));
            string sentence = RecordingHealthNotice.StatusSentence(TraceRecordingHealth.Snapshot());
            _out.WriteLine(sentence);
            Assert.Contains("2 ", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\t\a.txt", sentence, StringComparison.Ordinal);
            Assert.Contains("Control J", sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void A_failed_sink_is_said_as_stopped_not_off()
        {
            Invoke("NoteSink", TraceSinkState.Failed, "disk full", @"C:\t\JJFlexRadioTrace.txt", Guid.NewGuid(), 1L);
            string sentence = RecordingHealthNotice.StatusSentence(TraceRecordingHealth.Snapshot());
            _out.WriteLine(sentence);
            Assert.Contains("stopped", sentence, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("disk full", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain(" off", sentence, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_snapshot_lines_read_as_label_and_value_and_leak_no_key()
        {
            Invoke("NoteSink", TraceSinkState.Recording, null, @"C:\t\JJFlexRadioTrace.txt", Guid.NewGuid(), 1L);
            Invoke("NoteDetached", TicketAt(@"C:\t\a.txt", recordWritten: false, tailUncertain: true));
            var lines = RecordingHealthNotice.SnapshotLines(TraceRecordingHealth.Snapshot());
            foreach (var (label, value) in lines)
            {
                _out.WriteLine(label + ": " + value);
                Assert.False(string.IsNullOrWhiteSpace(label));
                Assert.DoesNotContain("logging.recording", label + value, StringComparison.Ordinal);
            }
            Assert.Contains(lines, l => l.Value.Contains(@"C:\t\a.txt", StringComparison.Ordinal)
                                        && l.Value.Contains("last lines", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Value.Contains("recording", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The change reaches <see cref="OperationFailure"/> — the route the
        /// Problems list, the Diagnostics count and the one queued announcement
        /// all hang off — once per condition, with its own kind; a resolution
        /// is not reported; a sink failure is its own kind.
        /// </summary>
        [Fact]
        public void The_watch_reports_each_condition_once_through_the_failure_route()
        {
            var reports = new List<OperationFailureEventArgs>();
            void OnReported(object s, OperationFailureEventArgs e) { lock (reports) reports.Add(e); }
            OperationFailure.Reported += OnReported;
            RecordingHealthWatch.Install();
            try
            {
                TraceArchiveTicket ticket = TicketAt(@"C:\t\a.txt", recordWritten: false);
                Invoke("NoteDetached", ticket);
                Invoke("NoteDetached", ticket);                  // a repeat of the same ticket
                Invoke("NoteRecoveryRecordPersisted", ticket);   // resolved: not announced
                Invoke("NoteSink", TraceSinkState.Failed, "disk full", @"C:\t\JJFlexRadioTrace.txt", Guid.NewGuid(), 1L);
                Invoke("NoteSink", TraceSinkState.Failed, "disk full", @"C:\t\JJFlexRadioTrace.txt", Guid.NewGuid(), 1L); // same state again

                lock (reports)
                {
                    Assert.Equal(2, reports.Count);
                    Assert.Equal(FailureKind.RecordingRecoveryAtRisk, reports[0].Kind);
                    Assert.Contains(@"C:\t\a.txt", reports[0].Detail, StringComparison.Ordinal);
                    Assert.Equal(FailureKind.RecordingStopped, reports[1].Kind);
                    Assert.Contains("disk full", reports[1].Detail, StringComparison.Ordinal);
                    foreach (OperationFailureEventArgs e in reports)
                    {
                        _out.WriteLine(e.What + " — " + e.Detail);
                        Assert.DoesNotContain("logging.recording", e.What + e.Detail, StringComparison.Ordinal);
                        Assert.False(e.What.EndsWith(".", StringComparison.Ordinal),
                            "the What clause is followed by the announcement's own full stop");
                    }
                }
            }
            finally { OperationFailure.Reported -= OnReported; }
        }

        /// <summary>
        /// <b>Sol's review of H7, finding 4.</b> A ticket reported while its
        /// archive was pending is reported ONCE as a new problem, keyed; when
        /// the archive then fails, the same key is UPDATED with the
        /// archive-failed sentences — "still filing it in the background" is
        /// gone, "could not be filed" is there — and nothing is reported as
        /// new a second time. The same holds when the earlier condition had
        /// resolved (the worker's retry wrote the record) and the archive
        /// failed afterwards: under H7 that second raise was swallowed by the
        /// once-per-ticket rule and the operator's entry never learned of it.
        /// </summary>
        [Fact]
        public void A_pending_entry_follows_its_ticket_into_archive_failure_without_a_second_announcement()
        {
            var reports = new List<OperationFailureEventArgs>();
            void OnReported(object s, OperationFailureEventArgs e) { lock (reports) reports.Add(e); }
            OperationFailure.Reported += OnReported;
            RecordingHealthWatch.Install();
            try
            {
                TraceArchiveTicket ticket = TicketAt(@"C:\t\a.txt", recordWritten: false);
                Invoke("NoteDetached", ticket);
                Invoke("NoteArchiveOutcome", ticket, new TraceArchiveCompletion
                {
                    TicketId = ticket.TicketId,
                    ArchiveCommitted = false,
                    RawRetained = true,
                    RawPath = ticket.SourcePath,
                    FailureStage = "compress",
                    FailureMessage = "disk full",
                });

                OperationFailureEventArgs first, second;
                lock (reports)
                {
                    Assert.Equal(2, reports.Count);
                    first = reports[0];
                    second = reports[1];
                }
                _out.WriteLine(first.What + " — " + first.Detail);
                _out.WriteLine(second.What + " — " + second.Detail);

                Assert.False(first.IsUpdate);
                Assert.Equal(RecordingHealthWatch.KeyFor(ticket.TicketId), first.Key);
                Assert.Contains("still filing", first.Detail, StringComparison.Ordinal);

                Assert.True(second.IsUpdate);
                Assert.Equal(first.Key, second.Key);
                Assert.Equal(FailureKind.RecordingRecoveryAtRisk, second.Kind);
                Assert.Contains("could not be filed", second.What, StringComparison.Ordinal);
                Assert.Contains("compressed", second.Detail, StringComparison.Ordinal);
                Assert.Contains(@"C:\t\a.txt", second.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("still filing", second.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("will not be filed", second.Detail, StringComparison.Ordinal);

                // Resolved, then failed: the fresh raise is an update too.
                TraceArchiveTicket later = TicketAt(@"C:\t\b.txt", recordWritten: false);
                Invoke("NoteDetached", later);
                Invoke("NoteRecoveryRecordPersisted", later);
                Assert.DoesNotContain(TraceRecordingHealth.Snapshot().Unresolved, c => c.TicketId == later.TicketId);
                Invoke("NoteArchiveOutcome", later, new TraceArchiveCompletion
                {
                    TicketId = later.TicketId, ArchiveCommitted = false, RawRetained = true,
                    RawPath = later.SourcePath, FailureStage = "manifest", FailureMessage = "locked",
                });
                lock (reports)
                {
                    Assert.Equal(4, reports.Count);
                    Assert.False(reports[2].IsUpdate);
                    Assert.True(reports[3].IsUpdate);
                    Assert.Equal(reports[2].Key, reports[3].Key);
                    Assert.Contains("could not be filed", reports[3].What, StringComparison.Ordinal);
                    // Exactly one NEW report per ticket, ever.
                    Assert.Equal(2, reports.Count(r => !r.IsUpdate));
                }
            }
            finally { OperationFailure.Reported -= OnReported; }
        }

        /// <summary>
        /// The drop dialog's notice carries ONE caveat, the tail, as one more
        /// paragraph, only when it holds — the ordinary window's prose is
        /// untouched — and that paragraph claims neither that the index file
        /// failed nor that the file is complete (Sol's review of H7, finding
        /// 3: the H7 draft said both, and either could be false).
        /// </summary>
        [Fact]
        public void The_drop_notice_adds_its_tail_caveat_only_when_the_tail_is_uncertain()
        {
            var plain = new CaptureSealNotice("6300inshack", @"C:\Traces\one.zip", true, Guid.NewGuid(), tailUncertain: false);
            var shortTail = new CaptureSealNotice("6300inshack", @"C:\Traces\one.zip", true, Guid.NewGuid(), tailUncertain: true);
            Assert.Equal(string.Empty, plain.TailCaveat);
            Assert.DoesNotContain("last lines", plain.Explanation, StringComparison.Ordinal);
            _out.WriteLine(shortTail.Explanation);
            Assert.StartsWith(plain.Explanation, shortTail.Explanation, StringComparison.Ordinal);
            Assert.Contains(shortTail.TailCaveat, shortTail.Explanation, StringComparison.Ordinal);
            Assert.EndsWith(".", shortTail.TailCaveat.TrimEnd(), StringComparison.Ordinal);
            Assert.DoesNotContain("logging.capture", shortTail.TailCaveat, StringComparison.Ordinal);
            Assert.Contains(shortTail.TailCaveat, shortTail.AsText(), StringComparison.Ordinal);
            // Neither false claim of the H7 draft survives.
            Assert.DoesNotContain("index file", shortTail.TailCaveat, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("complete", shortTail.TailCaveat, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A ticket whose ONLY problem is its tail — index file written, the
        /// archive still to come — is described as that, not as an index
        /// file that failed. Under H7 it got the record-failed detail, which
        /// said the index was not written and the recording would not be
        /// filed automatically; both were false for it.
        /// </summary>
        [Fact]
        public void A_tail_only_condition_is_not_described_as_a_missing_index_file()
        {
            TraceArchiveTicket ticket = TicketAt(@"C:\t\a.txt", recordWritten: true, tailUncertain: true);
            Invoke("NoteDetached", ticket);
            TraceRecoveryCondition c = Assert.Single(TraceRecordingHealth.Snapshot().Unresolved);
            string what = RecordingHealthNotice.ConditionWhat(c);
            string detail = RecordingHealthNotice.ConditionDetail(c);
            _out.WriteLine(what + " — " + detail);
            Assert.Contains("last lines", what, StringComparison.Ordinal);
            Assert.Contains(@"C:\t\a.txt", detail, StringComparison.Ordinal);
            Assert.Contains("last lines may not have reached the disk", detail, StringComparison.Ordinal);
            Assert.DoesNotContain("still filing", detail, StringComparison.Ordinal);
            Assert.DoesNotContain("will not be filed", detail, StringComparison.Ordinal);
            Assert.DoesNotContain("logging.recording", what + detail, StringComparison.Ordinal);

            // Positive control: with the index file NOT written, the same
            // ticket gets the record-failed sentences.
            TraceRecordingHealth.ResetForTests();
            Invoke("NoteDetached", TicketAt(@"C:\t\b.txt", recordWritten: false, tailUncertain: true));
            TraceRecoveryCondition r = Assert.Single(TraceRecordingHealth.Snapshot().Unresolved);
            Assert.Contains("not yet safely filed", RecordingHealthNotice.ConditionWhat(r), StringComparison.Ordinal);
            Assert.Contains("still filing", RecordingHealthNotice.ConditionDetail(r), StringComparison.Ordinal);
        }
    }
}
