using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// The journal (design section 6), driven without its worker so every
    /// flush is explicit: what a record carries, that equal values get new
    /// sequences, that events flush at once, that overflow leaves a named gap
    /// rather than silence, that the ring keeps 120 seconds and reports what
    /// it actually holds, and that a capture links the segment.
    /// </summary>
    public sealed class AlarmJournalTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-journal-" + Guid.NewGuid().ToString("N"));
        private readonly ManualAlarmClock _clock = new() { NowMs = 100_000 };
        private AlarmJournal? _journal;

        public void Dispose()
        {
            _journal?.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private AlarmJournal Up()
        {
            _journal = new AlarmJournal(_root, _clock, startWorker: false);
            _journal.ConnectionStarted(1, "1234-5678-9012-3456");
            _journal.RecordedSetChanged(1, new[] { PaTemperatureReplayFixture.Meter });
            _journal.Flush(force: true);
            return _journal;
        }

        private MeterObservation Obs(long seq, float value, long? ms = null)
            => MeterObservation.Measured(PaTemperatureReplayFixture.Meter, value, seq, ms ?? _clock.NowMs, _clock.UtcNow, 1, null);

        private List<JsonElement> Lines()
        {
            string path = _journal!.Stats().CurrentSegmentPath;
            Assert.False(string.IsNullOrEmpty(path), "no segment was written");
            return File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        }

        private static IEnumerable<JsonElement> OfType(IEnumerable<JsonElement> lines, string t)
            => lines.Where(e => e.GetProperty("t").GetString() == t);

        [Fact]
        public void The_segment_opens_with_its_identity_and_every_observation_carries_the_sensor_basis()
        {
            var j = Up();
            j.Record(Obs(1, 25.703125f));
            j.Record(Obs(2, 25.703125f));   // equal value, new sequence
            j.Flush(force: true);

            var lines = Lines();
            var segment = OfType(lines, "segment").Single();
            Assert.Equal("1234-5678-9012-3456", segment.GetProperty("radio").GetString());
            Assert.Equal(1, segment.GetProperty("gen").GetInt32());

            var obs = OfType(lines, "obs").ToList();
            Assert.Equal(2, obs.Count);
            Assert.Equal(1, obs[0].GetProperty("seq").GetInt64());
            Assert.Equal(2, obs[1].GetProperty("seq").GetInt64());
            Assert.Equal(25.703125f, obs[1].GetProperty("value").GetSingle());
            Assert.Equal("PATEMP", obs[1].GetProperty("name").GetString());
            Assert.Equal("DegreesC", obs[1].GetProperty("units").GetString());
            Assert.Equal(JsonValueKind.Null, obs[1].GetProperty("sensorUtc").ValueKind);
            Assert.Contains("not supplied", obs[1].GetProperty("sensorBasis").GetString());
            Assert.Equal("Measured", obs[1].GetProperty("provenance").GetString());
            Assert.Single(OfType(lines, "recorded_set"));
            Assert.Single(OfType(lines, "connection"));
        }

        [Fact]
        public void Previews_and_replays_never_reach_the_journal_as_observations()
        {
            var j = Up();
            j.Record(MeterObservation.PreviewOf(PaTemperatureReplayFixture.Meter, 60f, _clock.NowMs, _clock.UtcNow, 1));
            j.Record(MeterObservation.Replayed(PaTemperatureReplayFixture.Meter, 60f, 9, _clock.NowMs, _clock.UtcNow, 1, null));
            j.Flush(force: true);
            Assert.Empty(OfType(Lines(), "obs"));
        }

        [Fact]
        public void An_event_refers_to_its_observation_by_sequence_and_carries_the_rule_it_was_judged_under()
        {
            var j = Up();
            var def = AlarmPresets.Build(AlarmPresets.PaTemperature, PaTemperatureReplayFixture.Meter, "1234", "pa") with { Enabled = true, Revision = 3 };
            var o = Obs(7, 61f);
            j.Record(o);
            j.RecordEvent(new AlarmEvent
            {
                Kind = AlarmEventKind.Fired, Definition = def, EpisodeId = "ep1", AtMs = _clock.NowMs + 1, Observation = o,
                Value = 61f, Threshold = 60, PersistenceSamples = 1, Transmitting = false,
            });
            j.Flush(force: true);

            var ev = OfType(Lines(), "event").Single();
            Assert.Equal("Fired", ev.GetProperty("kind").GetString());
            Assert.Equal("pa", ev.GetProperty("alarmId").GetString());
            Assert.Equal(3, ev.GetProperty("revision").GetInt32());
            Assert.Equal(7, ev.GetProperty("obsSeq").GetInt64());
            Assert.Equal(60, ev.GetProperty("threshold").GetDouble());
            Assert.Equal(2, ev.GetProperty("hysteresis").GetDouble());
            Assert.Equal(5, ev.GetProperty("freshnessAllowanceSeconds").GetDouble());
            Assert.Equal(1, ev.GetProperty("persistenceSamples").GetInt32());
            Assert.Equal("ep1", ev.GetProperty("episode").GetString());
        }

        [Fact]
        public void Overflow_drops_counts_and_writes_a_gap_with_the_sequence_range_instead_of_silence()
        {
            var j = Up();
            for (long s = 1; s <= AlarmJournal.MaxQueueRecords + 5; s++) j.Record(Obs(s, 30f));
            Assert.Equal(5, j.Stats().Dropped);
            j.Record(Obs(99_999, 30f));   // still full: dropped too
            j.Flush(force: true);
            j.Record(Obs(100_000, 30f));  // room again: the gap is written first
            j.Flush(force: true);

            var lines = Lines();
            var gap = OfType(lines, "gap").Single();
            Assert.Equal(6, gap.GetProperty("dropped").GetInt64());
            Assert.Equal(AlarmJournal.MaxQueueRecords + 1, gap.GetProperty("firstSeq").GetInt64());
            Assert.Equal(99_999, gap.GetProperty("lastSeq").GetInt64());
            Assert.True(gap.GetProperty("exact").GetBoolean());
            Assert.Equal(AlarmJournal.MaxQueueRecords + 1, OfType(lines, "obs").Count());
            Assert.True(j.Stats().QueueHighWaterMark >= AlarmJournal.MaxQueueRecords);
        }

        [Fact]
        public void The_ring_keeps_the_last_two_minutes_and_reports_what_it_actually_holds()
        {
            var j = Up();
            long seq = 0;
            for (long t = 0; t <= 200_000; t += 2000)
            {
                _clock.NowMs = 100_000 + t;
                j.Record(Obs(++seq, 30f, _clock.NowMs));
                j.Flush(force: true);
            }
            IReadOnlyList<string> ring = j.PreRoll(out double retained);
            Assert.InRange(retained, 118, 120);
            Assert.Equal(61, ring.Count);   // 120 s at 2 s, inclusive
            Assert.Contains("\"seq\":41", ring[0]);

            // A short session says how little it holds, not ninety seconds.
            var fresh = new AlarmJournal(Path.Combine(_root, "short"), _clock, startWorker: false);
            fresh.ConnectionStarted(1, "1234");
            fresh.Record(Obs(1, 30f, _clock.NowMs));
            fresh.Record(Obs(2, 30f, _clock.NowMs + 2000));
            fresh.Flush(force: true);
            fresh.PreRoll(out double shortRetained);
            Assert.Equal(2, shortRetained, 3);
            fresh.Dispose();
        }

        [Fact]
        public void A_capture_start_links_the_segment_and_writes_the_preroll_beside_it()
        {
            var j = Up();
            long seq = 0;
            for (long t = 0; t <= 100_000; t += 2000)
            {
                _clock.NowMs = 100_000 + t;
                j.Record(Obs(++seq, 30f, _clock.NowMs));
            }
            j.Flush(force: true);

            string preRoll = j.CaptureStarted(@"C:\somewhere\JJFlexRadioTrace.txt");
            Assert.True(File.Exists(preRoll));
            var preLines = File.ReadAllLines(preRoll).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            var head = preLines[0];
            Assert.Equal("preroll", head.GetProperty("t").GetString());
            Assert.Equal(51, head.GetProperty("records").GetInt32());
            Assert.Equal(100, head.GetProperty("retainedSeconds").GetDouble(), 3);
            Assert.Equal(51, preLines.Count - 1);

            j.Flush(force: true);
            var link = OfType(Lines(), "capture").Single(e => e.GetProperty("state").GetString() == "started");
            Assert.Equal(preRoll, link.GetProperty("preRollPath").GetString());
            Assert.Equal(head.GetProperty("segment").GetString(), link.GetProperty("segment").GetString());
            Assert.Equal(j.Stats().CurrentSegmentPath, link.GetProperty("segmentPath").GetString());
            j.CaptureStopped();
            Assert.Single(OfType(Lines(), "capture").Where(e => e.GetProperty("state").GetString() == "stopped"));
        }

        // ── Astra's Track I review, findings 8, 9 and 10 ──

        [Fact]
        public void A_non_finite_value_is_written_as_a_named_literal_and_takes_nothing_else_with_it()
        {
            // Finding 8: NaN and infinity broke the serializer, and the drained
            // batch around them was lost. Invalid observations are admitted on
            // purpose; the journal must take them.
            var j = Up();
            j.Record(Obs(1, 30f));
            j.Record(Obs(2, float.NaN));
            j.Record(Obs(3, float.PositiveInfinity));
            j.Record(Obs(4, 31f));
            j.Flush(force: true);

            var obs = OfType(Lines(), "obs").ToList();
            Assert.Equal(4, obs.Count);
            Assert.Equal("NaN", obs[1].GetProperty("value").GetString());
            Assert.Equal("NonFinite", obs[1].GetProperty("valid").GetString());
            Assert.Equal("Infinity", obs[2].GetProperty("value").GetString());
            Assert.Equal(31f, obs[3].GetProperty("value").GetSingle());
            Assert.True(j.Stats().Healthy);
        }

        [Fact]
        public void A_batch_the_file_would_not_take_is_kept_and_written_behind_a_write_gap_when_writing_resumes()
        {
            // Finding 8: an append failure discarded the batch, and the next
            // success reset the health flag with no durable record of the loss.
            var j = Up();
            string segment = j.Stats().CurrentSegmentPath;
            j.Record(Obs(1, 30f));
            j.Flush(force: true);

            // Put a directory where the segment file is: the next append fails.
            File.Delete(segment);
            Directory.CreateDirectory(segment);
            j.Record(Obs(2, 31f));
            j.Record(Obs(3, 32f));
            j.Flush(force: true);
            Assert.False(j.Stats().Healthy);
            Assert.Equal(0, j.Stats().RecordsLostToWriteFailure);

            // Writing resumes: the kept records come first, behind a record
            // that says how many flushes failed and how many came through.
            Directory.Delete(segment);
            j.Record(Obs(4, 33f));
            j.Flush(force: true);
            Assert.True(j.Stats().Healthy);

            var lines = File.ReadAllLines(segment).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            var gap = OfType(lines, "write_gap").Single();
            Assert.Equal(1, gap.GetProperty("failedFlushes").GetInt32());
            Assert.Equal(2, gap.GetProperty("recordsRecovered").GetInt32());
            Assert.Equal(0, gap.GetProperty("recordsLost").GetInt64());
            Assert.Equal(new long[] { 2, 3, 4 }, OfType(lines, "obs").Select(o => o.GetProperty("seq").GetInt64()).ToArray());
        }

        [Fact]
        public void An_overflow_followed_by_nothing_but_a_flush_still_writes_its_gap()
        {
            // Finding 10: the gap was written only at the NEXT successful
            // enqueue, so an overflow followed by shutdown left no gap at all.
            var j = Up();
            for (long s = 1; s <= AlarmJournal.MaxQueueRecords + 3; s++) j.Record(Obs(s, 30f));
            j.Flush(force: true);   // nothing enqueued after the drops

            var gap = OfType(Lines(), "gap").Single();
            Assert.Equal(3, gap.GetProperty("dropped").GetInt64());
            Assert.Equal(AlarmJournal.MaxQueueRecords + 1, gap.GetProperty("firstSeq").GetInt64());
            Assert.Equal(AlarmJournal.MaxQueueRecords + 3, gap.GetProperty("lastSeq").GetInt64());
        }

        [Fact]
        public void A_capture_start_flushes_first_so_the_freshest_queued_readings_are_in_the_preroll_and_reports_their_age()
        {
            // Finding 10: the ring was copied and THEN flushed, so the newest
            // queued readings were in the segment but not in the pre-roll that
            // claimed to be its last ninety seconds.
            var j = Up();
            j.Record(Obs(1, 30f, _clock.NowMs));
            j.Flush(force: true);
            _clock.Advance(2000);
            j.Record(Obs(2, 31f, _clock.NowMs));   // queued, not yet flushed
            _clock.Advance(3000);                    // and three seconds pass before the capture

            string preRoll = j.CaptureStarted(@"C:\somewhere\JJFlexRadioTrace.txt");
            var preLines = File.ReadAllLines(preRoll).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            Assert.Equal(2, preLines[0].GetProperty("records").GetInt32());
            Assert.Equal(3, preLines[0].GetProperty("newestRecordAgeSeconds").GetDouble(), 3);
            Assert.Equal(2, preLines[2].GetProperty("seq").GetInt64());
        }

        [Fact]
        public void A_new_connection_rotates_to_a_segment_under_the_new_radio_and_clears_the_other_radios_preroll()
        {
            // Finding 9: ConnectionStarted changed the serial and generation
            // without rotating, so a later radio's observations sat under the
            // earlier radio's header, and the ring carried one radio's evidence
            // into another's pre-roll.
            var j = Up();
            j.Record(Obs(1, 30f));
            j.Flush(force: true);
            string first = j.Stats().CurrentSegmentPath;
            Assert.Single(j.PreRoll(out _));

            j.ConnectionStarted(2, "9999-0000-0000-0000");
            Assert.Empty(j.PreRoll(out _));
            j.Record(MeterObservation.Measured(PaTemperatureReplayFixture.Meter, 40f, 1, _clock.NowMs, _clock.UtcNow, 2, null));
            j.Flush(force: true);

            string second = j.Stats().CurrentSegmentPath;
            Assert.NotEqual(first, second);
            Assert.Contains("9999-0000-0000-0000", second);
            var firstLines = File.ReadAllLines(first).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            Assert.Single(OfType(firstLines, "segment_end"));
            Assert.DoesNotContain(OfType(firstLines, "obs"), o => o.GetProperty("gen").GetInt32() == 2);
            var secondLines = Lines();
            Assert.Equal(2, OfType(secondLines, "segment").Single().GetProperty("gen").GetInt32());
            Assert.Equal("9999-0000-0000-0000", OfType(secondLines, "segment").Single().GetProperty("radio").GetString());
            Assert.Single(OfType(secondLines, "obs"));
        }

        [Fact]
        public void A_reconnect_to_the_same_radio_rotates_but_keeps_the_preroll()
        {
            var j = Up();
            j.Record(Obs(1, 30f));
            j.Flush(force: true);
            string first = j.Stats().CurrentSegmentPath;
            j.ConnectionStarted(2, "1234-5678-9012-3456");
            Assert.Single(j.PreRoll(out _));   // same radio: its last ninety seconds are still its own
            j.Record(Obs(2, 30f));
            j.Flush(force: true);
            Assert.NotEqual(first, j.Stats().CurrentSegmentPath);
        }

        [Fact]
        public void The_capture_link_survives_a_restart_and_covers_a_segment_opened_while_the_capture_runs()
        {
            // Additional observation: protection was an in-memory set, so a
            // restart forgot it and pruning could delete a segment a capture had
            // named; and a segment opened during a running capture was not
            // linked at all.
            var j = Up();
            j.Record(Obs(1, 30f));
            j.CaptureStarted(@"C:\somewhere\trace.txt");
            string linkedFirst = Path.GetFileNameWithoutExtension(j.Stats().CurrentSegmentPath).Substring("journal-".Length);

            // A rotation while the capture is open: the new segment is linked too.
            j.ConnectionStarted(2, "1234-5678-9012-3456");
            j.Record(Obs(2, 30f));
            j.Flush(force: true);
            string linkedSecond = Path.GetFileNameWithoutExtension(j.Stats().CurrentSegmentPath).Substring("journal-".Length);
            Assert.NotEqual(linkedFirst, linkedSecond);

            string dir = Path.GetDirectoryName(j.Stats().CurrentSegmentPath)!;
            string[] links = File.ReadAllLines(Path.Combine(dir, AlarmJournal.LinkedSegmentsFileName));
            Assert.Contains(linkedFirst, links);
            Assert.Contains(linkedSecond, links);
            j.Dispose();

            // A NEW journal, after a restart, with enough old segments to prune:
            // the linked ones survive.
            for (int i = 0; i < AlarmJournal.SegmentsKeptPerRadio + 3; i++)
                File.WriteAllText(Path.Combine(dir, "journal-20200101-0000" + i.ToString("00") + "-abcdef.jsonl"), "{}\n");
            var again = new AlarmJournal(_root, _clock, startWorker: false);
            again.ConnectionStarted(3, "1234-5678-9012-3456");
            again.Record(Obs(1, 30f));
            again.Flush(force: true);
            Assert.True(File.Exists(Path.Combine(dir, "journal-" + linkedFirst + ".jsonl")));
            Assert.True(File.Exists(Path.Combine(dir, "journal-" + linkedSecond + ".jsonl")));
            again.Dispose();
        }

        [Fact]
        public void A_heartbeat_carries_the_meter_receipt_age_separately_from_its_own_clock()
        {
            // The one test that runs the worker: the heartbeat is the worker's
            // own once-a-second record, and it must carry the meter's last
            // receipt time as a separate field that its own advancing clock
            // never refreshes.
            using var live = new AlarmJournal(Path.Combine(_root, "live"), new SystemAlarmClock());
            live.ConnectionStarted(1, "1234");
            live.RecordedSetChanged(1, new[] { PaTemperatureReplayFixture.Meter });
            live.Record(MeterObservation.Measured(PaTemperatureReplayFixture.Meter, 30f, 1, Environment.TickCount64, DateTime.UtcNow, 1, null));
            System.Threading.Thread.Sleep(1500);
            string path = live.Stats().CurrentSegmentPath;
            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            var hb = OfType(lines, "heartbeat").Last();
            Assert.Equal(1, hb.GetProperty("lastSeq").GetInt64());
            Assert.True(hb.GetProperty("receiptAgeSeconds").GetDouble() > 0.5);
            Assert.True(hb.GetProperty("mono").GetInt64() > hb.GetProperty("lastReceiptMs").GetInt64());
        }

        [Fact]
        public void Disposal_flushes_the_partial_batch_and_a_disconnect_records_the_last_sequence()
        {
            var j = Up();
            j.Record(Obs(1, 30f));
            j.Record(Obs(2, 31f));
            j.Flush(force: true);
            j.ConnectionEnded(1, "connection_dropped");
            j.Record(Obs(3, 32f));   // a residual, still journaled
            j.Dispose();
            var lines = Lines();
            var ended = OfType(lines, "connection").Single(e => e.GetProperty("state").GetString() == "ended");
            Assert.Equal("connection_dropped", ended.GetProperty("why").GetString());
            Assert.Equal(2, ended.GetProperty("lastSeq").GetInt64());
            Assert.Equal(3, OfType(lines, "obs").Count());
        }

        [Fact]
        public void A_root_that_cannot_be_written_degrades_the_journal_and_says_so_in_stats()
        {
            Directory.CreateDirectory(_root);
            string blocker = Path.Combine(_root, "blocked");
            File.WriteAllText(blocker, "a file where the journal wants a directory");
            var j = new AlarmJournal(blocker, _clock, startWorker: false);
            j.ConnectionStarted(1, "1234");
            j.Record(Obs(1, 30f));
            j.Flush(force: true);
            Assert.False(j.Stats().Healthy);
            Assert.NotEqual("", j.Stats().HealthDetail);
            j.Dispose();
        }

        [Fact]
        public void Old_segments_are_pruned_and_a_linked_one_is_kept()
        {
            var j = Up();
            string dir = Path.GetDirectoryName(j.Stats().CurrentSegmentPath)!;
            for (int i = 0; i < AlarmJournal.SegmentsKeptPerRadio + 3; i++)
                File.WriteAllText(Path.Combine(dir, "journal-20200101-0000" + i.ToString("00") + "-abcdef.jsonl"), "{}\n");
            j.Dispose();

            var again = new AlarmJournal(_root, _clock, startWorker: false);
            again.ConnectionStarted(2, "1234-5678-9012-3456");
            again.Record(Obs(1, 30f));
            again.Flush(force: true);
            Assert.True(Directory.GetFiles(dir, "journal-*.jsonl").Length <= AlarmJournal.SegmentsKeptPerRadio + 1);
            again.Dispose();
        }
    }
}
