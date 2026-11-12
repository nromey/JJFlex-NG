using System;
using System.IO;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// Settings, Diagnostics shows the alarm journal's rate and drops in one
    /// sentence (#595, ruled by Noel 2026-09-22), and says plainly when no
    /// journal is running.
    /// </summary>
    // Lexicon resolves the process-wide settings root for its overlay, so
    // this class runs with the others that own that state (#232).
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AlarmJournalReadoutTests
    {
        private static AlarmJournalStats Stats(long written = 4512, long dropped = 0, int ring = 1201,
            double ringSeconds = 100, bool healthy = true, string detail = "", long lost = 0, long waiting = 0)
            => new AlarmJournalStats(ObservationsRecorded: written, EventsRecorded: 0, RecordsWritten: written,
                BytesWritten: written * 80, Dropped: dropped, QueueHighWaterMark: 3, RetainedRingSeconds: ringSeconds,
                RingRecords: ring, CurrentSegmentPath: "x", Healthy: healthy, HealthDetail: detail,
                RecordsLostToWriteFailure: lost, RecordsUnserializable: 0, RecordsRetainedUnwritten: waiting);

        [Fact]
        public void With_no_radio_it_says_no_journal_is_running_and_why()
        {
            string s = AlarmJournalReadout.Sentence(radioAttached: false, recordedMeters: 3, stats: Stats());
            Assert.Equal(Lexicon.Get("settings.diagnostics.alarm_journal.no_radio"), s);
            Assert.Contains("no radio", s, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_radio_with_nothing_armed_is_not_reported_as_recording()
        {
            // The journal exists at every attach, armed or not; with nothing
            // recorded it receives nothing, and the line must not show its
            // zero counts as though it were keeping up.
            string s = AlarmJournalReadout.Sentence(true, recordedMeters: 0, stats: Stats(written: 0, ring: 0, ringSeconds: 0));
            Assert.Equal(Lexicon.Get("settings.diagnostics.alarm_journal.nothing_armed"), s);
        }

        [Fact]
        public void A_radio_with_no_journal_says_so_rather_than_recording()
        {
            string s = AlarmJournalReadout.Sentence(true, recordedMeters: 2, stats: null);
            Assert.Equal(Lexicon.Get("settings.diagnostics.alarm_journal.unavailable"), s);
        }

        [Fact]
        public void Recording_gives_the_rate_over_the_ring_and_the_written_and_dropped_counts()
        {
            // 1,201 records spanning 100 seconds: 1,200 intervals, 12 a second.
            string s = AlarmJournalReadout.Sentence(true, 2, Stats(written: 4512, dropped: 7, ring: 1201, ringSeconds: 100));
            Assert.Contains("12", s, StringComparison.Ordinal);
            Assert.Contains(4512.ToString("N0"), s, StringComparison.Ordinal);
            Assert.Contains("7", s, StringComparison.Ordinal);
            Assert.DoesNotContain("{", s, StringComparison.Ordinal);
            Assert.Equal(Lexicon.Get("settings.diagnostics.alarm_journal.recording",
                ("rate", Lexicon.Get("settings.diagnostics.alarm_journal.rate", ("perSecond", "12"))),
                ("written", 4512.ToString("N0")), ("dropped", "7")), s);
        }

        [Fact]
        public void The_rate_comes_from_the_ring_and_is_withheld_when_there_is_too_little_of_it()
        {
            Assert.Equal(12.0, AlarmJournalReadout.RecordsPerSecond(Stats(ring: 1201, ringSeconds: 100))!.Value, 6);
            Assert.Equal(2.5, AlarmJournalReadout.RecordsPerSecond(Stats(ring: 6, ringSeconds: 2))!.Value, 6);
            Assert.Null(AlarmJournalReadout.RecordsPerSecond(Stats(ring: 1, ringSeconds: 0)));
            Assert.Null(AlarmJournalReadout.RecordsPerSecond(Stats(ring: 5, ringSeconds: 0.4)));

            string s = AlarmJournalReadout.Sentence(true, 1, Stats(written: 3, ring: 3, ringSeconds: 0.2));
            Assert.Contains(Lexicon.Get("settings.diagnostics.alarm_journal.rate_not_yet"), s, StringComparison.Ordinal);
        }

        [Fact]
        public void A_failing_journal_says_what_is_lost_and_waiting_and_the_last_error()
        {
            string s = AlarmJournalReadout.Sentence(true, 1,
                Stats(healthy: false, detail: "disk full", lost: 40, waiting: 9, dropped: 2));
            Assert.Equal(Lexicon.Get("settings.diagnostics.alarm_journal.recording_failing",
                ("rate", Lexicon.Get("settings.diagnostics.alarm_journal.rate", ("perSecond", "12"))),
                ("written", 4512.ToString("N0")), ("dropped", "2"), ("lost", "40"), ("waiting", "9"),
                ("detail", "disk full")), s);
            Assert.Contains("disk full", s, StringComparison.Ordinal);
        }

        // ── The tab, read as source: the line is never spoken, and is read off the UI thread ──

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), Path.Combine(parts)));

        private static string Body(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "Could not find '" + signature + "'; re-anchor this rule.");
            int open = src.IndexOf('{', at + signature.Length), depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(at, i - at + 1);
            }
            Assert.Fail("Unbalanced braces after '" + signature + "'.");
            return "";
        }

        [Fact]
        public void The_tab_reads_the_journal_off_the_UI_thread_when_shown_and_never_speaks_it()
        {
            string code = Read("JJFlexWpf", "Dialogs", "SettingsDialog.Diagnostics.cs");
            string line = Body(code, "private void RefreshAlarmJournalLine()");
            int run = line.IndexOf("Task.Run(", StringComparison.Ordinal);
            int stats = line.IndexOf("journal?.Stats()", StringComparison.Ordinal);
            Assert.True(run > 0 && stats > run, "Stats() must be read inside the Task.Run, never on the UI thread");
            Assert.Contains("Dispatcher.BeginInvoke(", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Speak", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Timer", line, StringComparison.Ordinal);

            Assert.Contains("RefreshAlarmJournalLine();", Body(code, "private void DiagnosticsPanel_Loaded("), StringComparison.Ordinal);
            string visible = Body(code, "private void DiagnosticsPanel_IsVisibleChanged(");
            Assert.Contains("RefreshAlarmJournalLine();", visible, StringComparison.Ordinal);
            int speak = visible.IndexOf("DiagnosticsBridge.Speak", StringComparison.Ordinal);
            Assert.True(speak > 0, "the tab's arrival sentence has moved; re-anchor this rule");
            Assert.DoesNotContain("AlarmJournal", visible.Substring(speak), StringComparison.Ordinal);

            string xaml = Read("JJFlexWpf", "Dialogs", "SettingsDialog.xaml");
            int at = xaml.IndexOf("x:Name=\"DiagAlarmJournalText\"", StringComparison.Ordinal);
            Assert.True(at > 0, "the alarm journal line is gone from the Diagnostics tab");
            string element = xaml.Substring(at, xaml.IndexOf("/>", at, StringComparison.Ordinal) - at);
            Assert.Contains("local:StatusTextName.Label=", element, StringComparison.Ordinal);
            Assert.DoesNotContain("AutomationProperties.Name", element, StringComparison.Ordinal);
            Assert.DoesNotContain("LiveSetting", element, StringComparison.Ordinal);
        }
    }
}
