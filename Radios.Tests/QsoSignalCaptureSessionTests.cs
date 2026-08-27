using System;
using System.Collections.Generic;
using System.Linq;
using Radios.SignalCapture;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The capture session (#271): the buffer, the stop, and the record it
    /// produces — under an injected clock, the FrequencyEchoGuard pattern, so
    /// nothing here waits.
    /// </summary>
    public sealed class QsoSignalCaptureSessionTests
    {
        private static readonly DateTime T0 = new(2026, 8, 26, 21, 14, 0, DateTimeKind.Utc);

        [Fact]
        public void StopBuildsACompleteRecordWithBothReportForms()
        {
            DateTime now = T0;
            var session = new QsoSignalCaptureSession(() => now, new Random(7));

            for (int i = 0; i < 300; i++)
            {
                now = T0.AddSeconds(i * 0.1);
                session.Add(-79.0, transmitting: false);
            }
            now = T0.AddSeconds(30);
            session.FrequencyText = "14.24 MHz";
            session.ModeText = "USB";
            session.SliceLetter = "A";
            session.RadioModelText = "FLEX-8600";

            var result = session.Stop("stopped by you");
            QsoSignalCaptureRecord record = result.Record;

            Assert.Equal(session.CaptureId, record.CaptureId);
            Assert.Equal(T0, record.StartedUtc);
            Assert.Equal(30.0, record.CaptureSeconds, 1);
            Assert.Equal(300, record.SampleOffsetsSeconds.Count);
            Assert.Equal(300, record.SampleDbm.Count);
            Assert.Empty(record.TransmitRanges);
            Assert.Contains("JJ Flexible QSO signal capture report", record.ReportText);
            Assert.Contains("Capture ID: " + record.CaptureId, record.ReportText);
            Assert.Contains("stopped by you", record.ReportText);
            Assert.Contains("Frequency: 14.24 MHz", record.ReportText);
            Assert.False(string.IsNullOrEmpty(record.ReportHtml));
            Assert.Equal("S7", record.PeakDisplay);
        }

        [Fact]
        public void TransmitStretchesBecomeRangesAndSurviveTheRoundTrip()
        {
            DateTime now = T0;
            var session = new QsoSignalCaptureSession(() => now, new Random(7));

            for (int i = 0; i < 600; i++)
            {
                now = T0.AddSeconds(i * 0.1);
                bool tx = i >= 200 && i < 400; // 20 s over in the middle
                session.Add(tx ? -40.0 : -80.0, tx);
            }
            now = T0.AddSeconds(60);

            QsoSignalCaptureRecord record = session.Stop("stopped by you").Record;

            Assert.Single(record.TransmitRanges);

            // The flags reconstruct from the ranges, so a reload analyzes the
            // same window the live stop did.
            IReadOnlyList<QsoSignalSample> samples = record.ToSamples();
            Assert.Equal(200, samples.Count(s => s.LocalTransmit));
            var reAnalyzed = QsoSignalAnalysis.Analyze(
                samples.ToList(), record.CaptureSeconds, record.BufferFilled);
            Assert.Equal(7, SMeterReading.FromDbm(reAnalyzed.PeakDbm));
        }

        [Fact]
        public void StopIsIdempotentAndTheBufferClosesWithIt()
        {
            DateTime now = T0;
            var session = new QsoSignalCaptureSession(() => now, new Random(7));
            now = T0.AddSeconds(1);
            session.Add(-80.0, false);

            var first = session.Stop("stopped by you");
            session.Add(-40.0, false); // must be refused: never added to after it ends
            var second = session.Stop("stopped again");

            Assert.Same(first, second);
            Assert.Single(first.Record.SampleDbm);
            Assert.False(session.IsRunning);
        }

        [Fact]
        public void AFullBufferStopsRecordingNotTheCaptureAndSaysSo()
        {
            DateTime now = T0;
            var session = new QsoSignalCaptureSession(() => now, new Random(7));
            for (int i = 0; i < QsoSignalCaptureSession.MaxSamples + 5; i++)
                session.Add(-80.0, false);

            Assert.True(session.IsRunning, "reaching the cap must not stop the capture");
            Assert.Equal(QsoSignalCaptureSession.MaxSamples, session.SampleCount);

            QsoSignalCaptureRecord record = session.Stop("stopped by you").Record;
            Assert.True(record.BufferFilled);
            Assert.Contains("later readings were not kept", record.ReportText);
        }

        // -------- the named-absence report shapes --------

        [Fact]
        public void AnEmptyCaptureReportNamesTheGapAsAGapNotAQuietBand()
        {
            DateTime now = T0;
            var session = new QsoSignalCaptureSession(() => now, new Random(7));
            now = T0.AddSeconds(45);
            QsoSignalCaptureRecord record = session.Stop("stopped by you").Record;

            Assert.Contains("No meter readings arrived during this capture", record.ReportText);
            Assert.Contains("not a quiet band", record.ReportText);
        }

        [Fact]
        public void UnreadContextIsNamedNeverOmitted()
        {
            DateTime now = T0;
            var session = new QsoSignalCaptureSession(() => now, new Random(7));
            now = T0.AddSeconds(10);
            // No context set: all four observations must still appear, each
            // saying it could not be read.
            QsoSignalCaptureRecord record = session.Stop("stopped by you").Record;

            Assert.Contains("Frequency: could not be read when the capture started.",
                record.ReportText);
            Assert.Contains("Mode: could not be read when the capture started.",
                record.ReportText);
            Assert.Contains("Slice: could not be read when the capture started.",
                record.ReportText);
            Assert.Contains("Radio: could not be read when the capture started.",
                record.ReportText);
        }

        [Fact]
        public void ATooShortCaptureReportSaysWhatWasLeftUndetermined()
        {
            DateTime now = T0;
            var session = new QsoSignalCaptureSession(() => now, new Random(7));
            for (int i = 0; i < 30; i++)
            {
                now = T0.AddSeconds(i * 0.1);
                session.Add(-80.0, false);
            }
            now = T0.AddSeconds(3);
            QsoSignalCaptureRecord record = session.Stop("stopped by you").Record;

            Assert.Contains("too little to characterize a signal", record.ReportText);
            Assert.Contains("left undetermined rather than guessed at", record.ReportText);
        }

        [Fact]
        public void ContaminationFlagsAreNamedInTheReport()
        {
            DateTime now = T0;
            var session = new QsoSignalCaptureSession(() => now, new Random(7))
            {
                FrequencyChanged = true,
                SliceChanged = true,
            };
            for (int i = 0; i < 400; i++)
            {
                now = T0.AddSeconds(i * 0.1);
                session.Add(-80.0, false);
            }
            now = T0.AddSeconds(40);
            QsoSignalCaptureRecord record = session.Stop("stopped by you").Record;

            Assert.Contains("The receive frequency changed during this capture",
                record.ReportText);
            Assert.Contains("The active slice changed during this capture", record.ReportText);
            Assert.DoesNotContain("The mode changed", record.ReportText);
        }

        [Fact]
        public void RecordSurvivesJsonAndRejectsTheFuture()
        {
            DateTime now = T0;
            var session = new QsoSignalCaptureSession(() => now, new Random(7));
            now = T0.AddSeconds(5);
            session.Add(-80.0, true);
            QsoSignalCaptureRecord record = session.Stop("stopped by you").Record;
            record.Label = "Don on 40 meters";

            QsoSignalCaptureRecord reloaded = QsoSignalCaptureRecord.FromJson(record.ToJson());
            Assert.NotNull(reloaded);
            Assert.Equal(record.CaptureId, reloaded.CaptureId);
            Assert.Equal("Don on 40 meters", reloaded.Label);
            Assert.Equal("Don on 40 meters", reloaded.DisplayName);
            Assert.Equal(record.ReportText, reloaded.ReportText);
            Assert.Single(reloaded.TransmitRanges);

            Assert.Null(QsoSignalCaptureRecord.FromJson(
                record.ToJson().Replace("\"Schema\": 1", "\"Schema\": 2")));
            Assert.Null(QsoSignalCaptureRecord.FromJson("not json at all"));
            Assert.Null(QsoSignalCaptureRecord.FromJson(""));
        }

        [Fact]
        public void SummaryNamesAMissingPeakInsteadOfDroppingTheClause()
        {
            var record = new QsoSignalCaptureRecord
            {
                CaptureId = "AAA-222",
                StartedUtc = T0,
                CaptureSeconds = 45,
            };
            Assert.Contains("nothing measured", record.Summary());
            record.PeakDisplay = "S7";
            Assert.Contains("peaked S7", record.Summary());
        }
    }
}
