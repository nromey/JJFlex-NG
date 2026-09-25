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
        /// And the drop dialog's notice carries the same condition, as one
        /// more paragraph, only when it holds — the ordinary window's prose
        /// is untouched.
        /// </summary>
        [Fact]
        public void The_drop_notice_adds_its_caveat_only_when_recovery_is_at_risk()
        {
            var plain = new CaptureSealNotice("6300inshack", @"C:\Traces\one.zip", true, Guid.NewGuid(), recoveryAtRisk: false);
            var atRisk = new CaptureSealNotice("6300inshack", @"C:\Traces\one.zip", true, Guid.NewGuid(), recoveryAtRisk: true);
            Assert.Equal(string.Empty, plain.RecoveryCaveat);
            Assert.DoesNotContain("index file", plain.Explanation, StringComparison.Ordinal);
            _out.WriteLine(atRisk.Explanation);
            Assert.StartsWith(plain.Explanation, atRisk.Explanation, StringComparison.Ordinal);
            Assert.Contains(atRisk.RecoveryCaveat, atRisk.Explanation, StringComparison.Ordinal);
            Assert.EndsWith(".", atRisk.RecoveryCaveat.TrimEnd(), StringComparison.Ordinal);
            Assert.DoesNotContain("logging.capture", atRisk.RecoveryCaveat, StringComparison.Ordinal);
            Assert.Contains(atRisk.RecoveryCaveat, atRisk.AsText(), StringComparison.Ordinal);
        }
    }
}
