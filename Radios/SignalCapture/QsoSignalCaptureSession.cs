#nullable enable
using System;
using System.Collections.Generic;
using Radios.Fixer;

namespace Radios.SignalCapture
{
    /// <summary>What <see cref="QsoSignalCaptureSession.Stop"/> hands back:
    /// the persisted shape and the analysis behind it, so the caller can speak
    /// the headline without re-deriving anything.</summary>
    public sealed class QsoSignalCaptureStopResult
    {
        internal QsoSignalCaptureStopResult(
            QsoSignalCaptureRecord record, QsoSignalAnalysisResult analysis)
        {
            Record = record;
            Analysis = analysis;
        }

        public QsoSignalCaptureRecord Record { get; }
        public QsoSignalAnalysisResult Analysis { get; }
    }

    /// <summary>
    /// One live capture: a buffer that readings drop into, and a single
    /// <see cref="Stop"/> that turns the window into a record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Runs until told (#271, ruled by Noel 2026-08-26).</b> Nothing in
    /// here ends a capture — no timer, no auto-stop. A capture that ended
    /// itself sixty seconds into a fade would produce a package that LOOKS
    /// authoritative and is not. What makes run-until-told safe is the
    /// running-cost registration the controller holds, not a clock in here.
    /// </para>
    /// <para>
    /// <b>A running capture never touches disk.</b> Only <see cref="Stop"/>
    /// produces a record; kill the app mid-capture and nothing persists, which
    /// is the ruled behaviour — a stored report is a record of a window that
    /// genuinely happened.
    /// </para>
    /// <para>
    /// The buffer is capped (<see cref="MaxSamples"/> — about three hours at
    /// the meter stream's measured 15 to 20 readings a second) so a forgotten
    /// capture cannot eat memory without bound. Reaching the cap does NOT stop
    /// the capture; it stops the recording, sets
    /// <see cref="QsoSignalCaptureRecord.BufferFilled"/>, and the report names
    /// it — quietly covering less than the window would look authoritative
    /// and not be.
    /// </para>
    /// <para>
    /// Takes an injected clock, the FrequencyEchoGuard pattern, so tests can
    /// prove time-dependent behaviour without waiting.
    /// </para>
    /// </remarks>
    public sealed class QsoSignalCaptureSession
    {
        /// <summary>See the class remarks.</summary>
        public const int MaxSamples = 250_000;

        private readonly object _gate = new object();
        private readonly Func<DateTime> _clockUtc;
        private readonly List<QsoSignalSample> _samples = new List<QsoSignalSample>();
        private bool _running = true;
        private bool _bufferFilled;
        private QsoSignalCaptureStopResult? _result;

        public QsoSignalCaptureSession(Func<DateTime>? clockUtc = null, Random? idRng = null)
        {
            _clockUtc = clockUtc ?? (() => DateTime.UtcNow);
            StartedUtc = _clockUtc();
            CaptureId = FixerRunId.New(idRng ?? Random.Shared);
        }

        /// <summary>Speakable id, assigned at start so the stop announcement
        /// can name the capture.</summary>
        public string CaptureId { get; }

        public DateTime StartedUtc { get; }

        // Context observations, filled by the controller at start; empty means
        // could-not-be-read, per the record's convention.
        public string FrequencyText { get; set; } = "";
        public string ModeText { get; set; } = "";
        public string SliceLetter { get; set; } = "";
        public string RadioModelText { get; set; } = "";

        // Contamination flags, latched by the controller when it notices drift.
        // Latched, never cleared: "changed during the capture" stays true even
        // if the operator tunes back.
        public bool FrequencyChanged { get; set; }
        public bool ModeChanged { get; set; }
        public bool SliceChanged { get; set; }

        public bool IsRunning { get { lock (_gate) return _running; } }

        public int SampleCount { get { lock (_gate) return _samples.Count; } }

        public double ElapsedSeconds => (_clockUtc() - StartedUtc).TotalSeconds;

        /// <summary>
        /// One reading. Cheap and non-blocking on purpose — this is called on
        /// FlexLib's meter thread, whose handler contract is documented on
        /// <c>FlexBase.MeterData</c>.
        /// </summary>
        public void Add(double dbm, bool transmitting)
        {
            double offset = (_clockUtc() - StartedUtc).TotalSeconds;
            lock (_gate)
            {
                if (!_running) return;
                if (_samples.Count >= MaxSamples)
                {
                    _bufferFilled = true;
                    return;
                }
                _samples.Add(new QsoSignalSample(offset, dbm, transmitting));
            }
        }

        /// <summary>
        /// End the window, analyze it, and build the record with both report
        /// forms baked. Idempotent: a second call returns the first result —
        /// a capture is never added to after it ends.
        /// </summary>
        /// <param name="endReason">A phrase completing "It ran two minutes and
        /// was {endReason}." — "stopped by you", "stopped from the exit
        /// prompt".</param>
        public QsoSignalCaptureStopResult Stop(string endReason)
        {
            List<QsoSignalSample> samples;
            DateTime endUtc;
            lock (_gate)
            {
                if (!_running && _result != null) return _result;
                _running = false;
                samples = new List<QsoSignalSample>(_samples);
                endUtc = _clockUtc();
            }

            double captureSeconds = (endUtc - StartedUtc).TotalSeconds;
            QsoSignalAnalysisResult analysis =
                QsoSignalAnalysis.Analyze(samples, captureSeconds, _bufferFilled);

            var record = new QsoSignalCaptureRecord
            {
                CaptureId = CaptureId,
                StartedUtc = StartedUtc,
                EndedUtc = endUtc,
                CaptureSeconds = captureSeconds,
                EndReason = endReason ?? "",
                BufferFilled = _bufferFilled,
                FrequencyText = FrequencyText,
                ModeText = ModeText,
                SliceLetter = SliceLetter,
                RadioModelText = RadioModelText,
                FrequencyChanged = FrequencyChanged,
                ModeChanged = ModeChanged,
                SliceChanged = SliceChanged,
            };

            // Offsets to the millisecond and readings to a hundredth of a dB —
            // both below the meter's own 1/128 dBm resolution's usefulness,
            // and they keep a long capture's JSON a third of the size.
            foreach (QsoSignalSample sample in samples)
            {
                record.SampleOffsetsSeconds.Add(Math.Round(sample.OffsetSeconds, 3));
                record.SampleDbm.Add(Math.Round(sample.Dbm, 2));
            }
            record.TransmitRanges.AddRange(TransmitRangesOf(samples));

            QsoSignalCaptureReport.Bake(record, analysis);

            var result = new QsoSignalCaptureStopResult(record, analysis);
            lock (_gate) _result = result;
            return result;
        }

        private static List<double[]> TransmitRangesOf(List<QsoSignalSample> samples)
        {
            var ranges = new List<double[]>();
            double start = -1.0, last = -1.0;
            foreach (QsoSignalSample s in samples)
            {
                if (s.LocalTransmit)
                {
                    if (start < 0) start = s.OffsetSeconds;
                    last = s.OffsetSeconds;
                }
                else if (start >= 0)
                {
                    ranges.Add(new[] { Math.Round(start, 3), Math.Round(last, 3) });
                    start = -1.0;
                }
            }
            if (start >= 0)
                ranges.Add(new[] { Math.Round(start, 3), Math.Round(last, 3) });
            return ranges;
        }
    }
}
