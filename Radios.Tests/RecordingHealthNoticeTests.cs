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
        /// all hang off — once per condition as NEW, with its own kind; a
        /// resolution arrives marked as a resolution and never as new (since
        /// H9; H8 did not report it at all, which is Sol's blocker 3); a sink
        /// failure is its own kind.
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
                Invoke("NoteRecoveryRecordPersisted", ticket);   // resolved: a resolution, never new
                Invoke("NoteSink", TraceSinkState.Failed, "disk full", @"C:\t\JJFlexRadioTrace.txt", Guid.NewGuid(), 1L);
                Invoke("NoteSink", TraceSinkState.Failed, "disk full", @"C:\t\JJFlexRadioTrace.txt", Guid.NewGuid(), 1L); // same state again

                lock (reports)
                {
                    Assert.Equal(3, reports.Count);
                    Assert.Equal(2, reports.Count(r => !r.IsUpdate));
                    Assert.Equal(FailureKind.RecordingRecoveryAtRisk, reports[0].Kind);
                    Assert.False(reports[0].IsUpdate);
                    Assert.Contains(@"C:\t\a.txt", reports[0].Detail, StringComparison.Ordinal);
                    Assert.True(reports[1].IsResolution);
                    Assert.Equal(reports[0].Key, reports[1].Key);
                    Assert.Equal(FailureKind.RecordingStopped, reports[2].Kind);
                    Assert.False(reports[2].IsUpdate);
                    Assert.Contains("disk full", reports[2].Detail, StringComparison.Ordinal);
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

                // Resolved, then failed: the resolution replaces the entry
                // (since H9), and the fresh raise is an update too — three
                // sentences on one key, one of them announced.
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
                    Assert.Equal(5, reports.Count);
                    Assert.False(reports[2].IsUpdate);
                    Assert.True(reports[3].IsResolution);
                    Assert.Contains("will now be filed on its own", reports[3].What, StringComparison.Ordinal);
                    Assert.True(reports[4].IsUpdate);
                    Assert.False(reports[4].IsResolution);
                    Assert.Equal(reports[2].Key, reports[3].Key);
                    Assert.Equal(reports[2].Key, reports[4].Key);
                    Assert.Contains("could not be filed", reports[4].What, StringComparison.Ordinal);
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
        ///
        /// <para>Track H8's version of this test asserted that the short-tail
        /// notice STARTED WITH the ordinary one — which pinned the saved
        /// paragraph's "everything up to the moment the connection went,
        /// including the last readings" as desired under an uncertain tail.
        /// That was Sol's blocker 2 against H8. The assertion now is the
        /// opposite: the ordinary notice's content promise is absent from the
        /// short-tail one.</para>
        /// </summary>
        [Fact]
        public void The_drop_notice_adds_its_tail_caveat_only_when_the_tail_is_uncertain()
        {
            var plain = new CaptureSealNotice("6300inshack", @"C:\Traces\one.zip", true, Guid.NewGuid(), tailUncertain: false);
            var shortTail = new CaptureSealNotice("6300inshack", @"C:\Traces\one.zip", true, Guid.NewGuid(), tailUncertain: true);
            Assert.Equal(string.Empty, plain.TailCaveat);
            Assert.DoesNotContain("last lines", plain.Explanation, StringComparison.Ordinal);
            Assert.DoesNotContain("stop short", plain.Explanation, StringComparison.Ordinal);
            _out.WriteLine(shortTail.Explanation);
            Assert.StartsWith(plain.WhatHappened, shortTail.Explanation, StringComparison.Ordinal);
            Assert.DoesNotContain(plain.WhatWasSaved, shortTail.Explanation, StringComparison.Ordinal);
            Assert.DoesNotContain("including the last readings", shortTail.Explanation, StringComparison.Ordinal);
            Assert.Contains(shortTail.TailCaveat, shortTail.Explanation, StringComparison.Ordinal);
            Assert.EndsWith(".", shortTail.TailCaveat.TrimEnd(), StringComparison.Ordinal);
            Assert.DoesNotContain("logging.capture", shortTail.TailCaveat, StringComparison.Ordinal);
            Assert.Contains(shortTail.TailCaveat, shortTail.AsText(), StringComparison.Ordinal);
            // Neither false claim of the H7 draft survives.
            Assert.DoesNotContain("index file", shortTail.TailCaveat, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("complete", shortTail.TailCaveat, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// <b>Sol's review of H8, blocker 2.</b> The WHOLE assembled notice,
        /// for every combination of the three facts it is given — the tail is
        /// uncertain, a successor opened, the sink had died before the drop —
        /// and the claims each paragraph makes hold under that combination:
        /// the last readings are promised only with a certain tail; recording
        /// having restarted is promised only when a successor opened; "a
        /// write failed as the recording was being closed" appears only when
        /// the sink was alive until the seal; the earlier-fault sentence only
        /// when it was not. Every paragraph ends in a full stop and leaks no
        /// key. The ordinary window (tail certain, successor opened) is the
        /// H7 prose, unchanged. Each assembled notice is written to the test
        /// output so a person can read it end to end.
        /// </summary>
        [Fact]
        public void The_assembled_drop_notice_makes_only_the_claims_its_facts_support()
        {
            var ordinary = new CaptureSealNotice("6300inshack", @"C:\Traces\one.zip", true, Guid.NewGuid());
            foreach (bool tail in new[] { false, true })
            foreach (bool successor in new[] { false, true })
            foreach (bool diedBefore in new[] { false, true })
            foreach (bool recordingNow in new[] { false, true })
            {
                var n = new CaptureSealNotice("6300inshack", @"C:\Traces\one.zip", successor, Guid.NewGuid(),
                                              tailUncertain: tail, sinkFailedBeforeDrop: diedBefore,
                                              recordingNow: recordingNow);
                string text = n.Explanation;
                _out.WriteLine("--- tail uncertain: " + tail + ", successor opened: " + successor
                               + ", sink died before the drop: " + diedBefore
                               + ", recording now: " + recordingNow);
                _out.WriteLine(text);
                _out.WriteLine(string.Empty);

                // The absolute promise about content is made only when it holds.
                Assert.Equal(!tail, text.Contains("including the last readings the radio sent: forward power", StringComparison.Ordinal));
                Assert.Equal(!tail, text.Contains("It holds everything up to the moment", StringComparison.Ordinal));
                Assert.Equal(tail, text.Contains("send it anyway", StringComparison.Ordinal));
                // What happens next "is being kept" is promised only by the
                // state NOW (Sol's review of H9, blocker 1); the seal's
                // successor bit only tells the two not-recording paragraphs
                // apart.
                Assert.Equal(recordingNow, text.Contains("has already started recording again", StringComparison.Ordinal));
                Assert.Equal(recordingNow, text.Contains("is being kept too", StringComparison.Ordinal));
                Assert.Equal(!recordingNow, text.Contains("what happens next is not being kept", StringComparison.Ordinal));
                Assert.Equal(!recordingNow && !successor, text.Contains("has not started recording again", StringComparison.Ordinal));
                Assert.Equal(!recordingNow && successor, text.Contains("did start recording again after the connection went, but it is not recording now", StringComparison.Ordinal));
                Assert.Equal(!recordingNow, text.Contains("Control J then Control R", StringComparison.Ordinal));
                // The caveat names the right cause, and only with an uncertain tail.
                bool atTheClose = tail && !diedBefore;
                bool earlier = tail && diedBefore;
                Assert.Equal(atTheClose, text.Contains("as the recording was being closed", StringComparison.Ordinal));
                Assert.Equal(earlier, text.Contains("had already stopped taking new lines", StringComparison.Ordinal));
                Assert.Equal(earlier, n.SinkFailedBeforeDrop);
                Assert.Equal(tail, text.Contains("One more thing.", StringComparison.Ordinal));
                // Every paragraph is a sentence or more, and nothing leaks.
                foreach (string paragraph in text.Split(new[] { Environment.NewLine + Environment.NewLine }, StringSplitOptions.None))
                {
                    Assert.False(string.IsNullOrWhiteSpace(paragraph));
                    Assert.EndsWith(".", paragraph.TrimEnd(), StringComparison.Ordinal);
                    Assert.DoesNotContain("logging.capture", paragraph, StringComparison.Ordinal);
                }
                Assert.Equal(tail ? 4 : 3, text.Split(new[] { Environment.NewLine + Environment.NewLine }, StringSplitOptions.None).Length);
                // The errand is always there, and the path is never in the prose.
                Assert.Contains("Send this file to Noel.", text, StringComparison.Ordinal);
                Assert.DoesNotContain(@"C:\Traces\one.zip", text, StringComparison.Ordinal);
                Assert.Contains(@"C:\Traces\one.zip", n.AsText(), StringComparison.Ordinal);
            }

            // The ordinary window is the H7 prose, untouched — through both
            // constructors: the six-argument one reads "recording now" as the
            // successor bit (the H9 meaning), and the seven-argument one is
            // what production builds.
            var same = new CaptureSealNotice("6300inshack", @"C:\Traces\one.zip", true, Guid.NewGuid(),
                                             tailUncertain: false, sinkFailedBeforeDrop: false);
            Assert.Equal(ordinary.Explanation, same.Explanation);
            Assert.Equal(ordinary.AsText(), same.AsText());
            var built = new CaptureSealNotice("6300inshack", @"C:\Traces\one.zip", true, Guid.NewGuid(),
                                              tailUncertain: false, sinkFailedBeforeDrop: false, recordingNow: true);
            Assert.Equal(ordinary.Explanation, built.Explanation);
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
            string detail = RecordingHealthNotice.ConditionDetail(c, TraceSinkState.Recording);
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
            Assert.Contains("still filing", RecordingHealthNotice.ConditionDetail(r, TraceSinkState.Recording), StringComparison.Ordinal);
        }

        /// <summary>
        /// <b>Sol's review of H8, blocker 3.</b> A ticket reported while its
        /// filing was pending, and then filed after all, has its Problems
        /// entry REPLACED — through a route that is marked as a resolution,
        /// keyed to the same entry, never a new report — with the sentence
        /// that is true now. Both resolutions are driven: the worker's retry
        /// writing the recovery record ("will now be filed on its own"), and
        /// the archive committing ("has now been filed"). "Still filing" is
        /// gone from both. Exactly one non-update report per ticket, ever,
        /// and no report at all for a resolution of a ticket this watch never
        /// reported (positive control that the route is keyed to reporting,
        /// not to resolving).
        /// </summary>
        [Fact]
        public void A_resolved_condition_replaces_its_Problems_entry_and_is_never_reported_as_new()
        {
            var reports = new List<OperationFailureEventArgs>();
            void OnReported(object s, OperationFailureEventArgs e) { lock (reports) reports.Add(e); }
            OperationFailure.Reported += OnReported;
            RecordingHealthWatch.Install();
            try
            {
                // Route one: the record failed at the seal, the worker's retry writes it.
                TraceArchiveTicket indexed = TicketAt(@"C:\t\a.txt", recordWritten: false);
                Invoke("NoteDetached", indexed);
                Invoke("NoteRecoveryRecordPersisted", indexed);
                Assert.DoesNotContain(TraceRecordingHealth.Snapshot().Unresolved, c => c.TicketId == indexed.TicketId);

                // Route two: the tail was short, the archive then commits.
                TraceArchiveTicket filed = TicketAt(@"C:\t\b.txt", recordWritten: true, tailUncertain: true);
                Invoke("NoteDetached", filed);
                Invoke("NoteArchiveOutcome", filed, new TraceArchiveCompletion
                {
                    TicketId = filed.TicketId, ArchiveCommitted = true,
                    ArchiveFullPath = @"C:\Traces\b.zip", RawRetained = false,
                });
                Assert.DoesNotContain(TraceRecordingHealth.Snapshot().Unresolved, c => c.TicketId == filed.TicketId);

                // Positive control: a resolution for a ticket never reported
                // produces no event of any kind.
                TraceArchiveTicket never = TicketAt(@"C:\t\c.txt", recordWritten: true);
                Invoke("NoteArchiveOutcome", never, new TraceArchiveCompletion { TicketId = never.TicketId, ArchiveCommitted = true });

                OperationFailureEventArgs[] all;
                lock (reports) all = reports.ToArray();
                foreach (OperationFailureEventArgs e in all) _out.WriteLine((e.IsResolution ? "[resolved] " : e.IsUpdate ? "[update] " : "[new] ") + e.What + " — " + e.Detail);

                Assert.Equal(4, all.Length);
                Assert.Equal(2, all.Count(r => !r.IsUpdate));
                Assert.DoesNotContain(all, e => (e.Detail + e.What).Contains(@"C:\t\c.txt", StringComparison.Ordinal));

                OperationFailureEventArgs a0 = all[0], a1 = all[1], b0 = all[2], b1 = all[3];
                Assert.False(a0.IsUpdate);
                Assert.Contains("still filing", a0.Detail, StringComparison.Ordinal);
                Assert.True(a1.IsResolution);
                Assert.True(a1.IsUpdate);
                Assert.Equal(a0.Key, a1.Key);
                Assert.Equal(RecordingHealthWatch.KeyFor(indexed.TicketId), a1.Key);
                Assert.Equal(FailureKind.RecordingRecoveryAtRisk, a1.Kind);
                Assert.Contains("will now be filed on its own", a1.What, StringComparison.Ordinal);
                Assert.Contains(@"C:\t\a.txt", a1.Detail, StringComparison.Ordinal);
                Assert.Contains("recovery index could not be written", a1.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("still filing", a1.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("has now been filed", a1.What, StringComparison.Ordinal);

                Assert.False(b0.IsUpdate);
                Assert.Contains("last lines", b0.What, StringComparison.Ordinal);
                Assert.True(b1.IsResolution);
                Assert.Equal(b0.Key, b1.Key);
                Assert.Contains("has now been filed", b1.What, StringComparison.Ordinal);
                Assert.Contains(@"C:\t\b.txt", b1.Detail, StringComparison.Ordinal);
                Assert.Contains("last lines may not have reached the disk", b1.Detail, StringComparison.Ordinal);   // the tail is still short
                Assert.DoesNotContain("still filing", b1.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("will finish filing", b1.Detail, StringComparison.Ordinal);
                foreach (OperationFailureEventArgs e in new[] { a1, b1 })
                {
                    Assert.DoesNotContain("logging.recording", e.What + e.Detail, StringComparison.Ordinal);
                    Assert.False(e.What.EndsWith(".", StringComparison.Ordinal));
                    Assert.EndsWith(".", e.Detail.TrimEnd(), StringComparison.Ordinal);
                }
            }
            finally { OperationFailure.Reported -= OnReported; }
        }

        /// <summary>
        /// <b>Sol's review of H8, blocker 3, second half.</b> The
        /// archive-failed detail says the log running now is not affected
        /// only when a log is running now; with the sink off or failed it
        /// says nothing about a live log. Driven through the real change so
        /// the sink state is the one the change carries, then directly.
        /// </summary>
        [Fact]
        public void The_archive_failed_detail_claims_a_running_log_only_when_one_is_running()
        {
            TraceArchiveTicket ticket = TicketAt(@"C:\t\a.txt", recordWritten: true);
            var failed = new TraceArchiveCompletion
            {
                TicketId = ticket.TicketId, ArchiveCommitted = false, RawRetained = true,
                RawPath = ticket.SourcePath, FailureStage = "compress", FailureMessage = "disk full",
            };

            // No sink note at all: the model's state is Off.
            var changes = new List<TraceRecordingHealthChange>();
            void OnChanged(TraceRecordingHealthChange c) { lock (changes) changes.Add(c); }
            TraceRecordingHealth.Changed += OnChanged;
            try
            {
                Invoke("NoteArchiveOutcome", ticket, failed);
                TraceRecordingHealthChange raised = Assert.Single(changes);
                Assert.Equal(TraceSinkState.Off, raised.Snapshot.SinkState);
                var off = RecordingHealthNotice.Announcement(raised).Value;
                _out.WriteLine("[sink off] " + off.What + " — " + off.Detail);
                Assert.Contains("could not be filed", off.What, StringComparison.Ordinal);
                Assert.Contains(@"C:\t\a.txt", off.Detail, StringComparison.Ordinal);
                Assert.Contains("compressed", off.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("running now", off.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("not affected", off.Detail, StringComparison.Ordinal);

                // Positive control: with a live sink published, the same
                // ticket's detail does say so.
                changes.Clear();
                TraceRecordingHealth.ResetForTests();
                Invoke("NoteSink", TraceSinkState.Recording, null, @"C:\t\JJFlexRadioTrace.txt", Guid.NewGuid(), 1L);
                Invoke("NoteArchiveOutcome", ticket, failed);
                TraceRecordingHealthChange live = changes.Single(c => c.Kind == TraceRecordingHealthChangeKind.ConditionRaised);
                Assert.Equal(TraceSinkState.Recording, live.Snapshot.SinkState);
                var on = RecordingHealthNotice.Announcement(live).Value;
                _out.WriteLine("[sink recording] " + on.What + " — " + on.Detail);
                Assert.Contains("The log that is running now is not affected", on.Detail, StringComparison.Ordinal);
            }
            finally { TraceRecordingHealth.Changed -= OnChanged; }

            // And directly, for a FAILED sink.
            TraceRecoveryCondition c = Assert.Single(TraceRecordingHealth.Snapshot().Unresolved);
            string failedSink = RecordingHealthNotice.ConditionDetail(c, TraceSinkState.Failed);
            Assert.DoesNotContain("running now", failedSink, StringComparison.Ordinal);
            Assert.Contains("If you are sending evidence to Noel", failedSink, StringComparison.Ordinal);
            Assert.DoesNotContain("logging.recording", failedSink, StringComparison.Ordinal);
        }
    }
}
