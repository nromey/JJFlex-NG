#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios.SignalCapture
{
    /// <summary>
    /// The QSO signal analyzer's wiring: taps the S-meter stream, keeps the
    /// one live <see cref="QsoSignalCaptureSession"/>, and declares it in the
    /// <see cref="RunningCostRegister"/> so run-until-told is safe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The plumbing already existed; this only listens.</b> The tap is
    /// <c>FlexBase.MeterData</c> — the identity-preserving feed the meter
    /// tones use — filtered to the LEVEL meter of the active slice. That is
    /// the un-truncated float dBm at the stream's full rate; the scalar
    /// <c>FlexBase.SMeter</c> path truncates to whole dB and converts to
    /// S-units, both of which would blunt QSB measurement. No new collection
    /// path, no polling.
    /// </para>
    /// <para>
    /// <b>S-METER ONLY, ruled by Noel 2026-08-26.</b> The analysis would run
    /// over any meter, but the offering does not generalise: "QSO signal
    /// analyzer" describes something you point at a contact, and a meter
    /// picker would make it a different product with a wrong name. If #124
    /// later wants the maths over other meters, take
    /// <see cref="QsoSignalAnalysis"/> — not this controller.
    /// </para>
    /// <para>
    /// <b>Follows the active slice</b> — the same signal the operator's
    /// Ctrl+S reads and their meter tones sound. If the active slice changes
    /// mid-capture the capture keeps following it and latches
    /// <c>SliceChanged</c>, which the report names as possible mixing.
    /// </para>
    /// <para>
    /// The running-cost registration is Notable with no auto-stop: the
    /// register's exit prompt and Ctrl+J, O are what make a measurement with
    /// no clock on it something the operator cannot silently leave going.
    /// Thresholds at fifteen minutes and one hour — bounds on something that
    /// actually grew, never a timer.
    /// </para>
    /// </remarks>
    public static class QsoSignalCaptureController
    {
        private static readonly object Gate = new object();
        private static QsoSignalCaptureSession? _session;
        private static FlexBase? _rig;
        private static IDisposable? _registration;

        private static ulong _startFrequency;
        private static string _startMode = "";
        private static string _startSlice = "";

        public static bool IsRunning
        {
            get { lock (Gate) return _session != null && _session.IsRunning; }
        }

        /// <summary>The live session's speakable id, for surfaces that need to
        /// name it. Null when nothing is running.</summary>
        public static string? RunningCaptureId
        {
            get { lock (Gate) return _session?.CaptureId; }
        }

        /// <summary>Start watching. False if a capture is already running —
        /// the caller should be toggling, not stacking.</summary>
        public static bool Start(FlexBase rig)
        {
            if (rig == null) throw new ArgumentNullException(nameof(rig));
            lock (Gate)
            {
                if (_session != null && _session.IsRunning) return false;

                var session = new QsoSignalCaptureSession();

                // Context observations — every read guarded, because an empty
                // value is an honest "could not be read" and a teardown-time
                // property throw must not kill the start.
                _startFrequency = SafeRead(() => rig.RXFrequency, 0UL);
                _startMode = SafeRead(() => rig.Mode, "") ?? "";
                _startSlice = SafeRead(() => rig.ActiveSliceLetter, "") ?? "";
                session.FrequencyText = _startFrequency > 0
                    ? FormatMHz(_startFrequency) : "";
                // The number as well as the words: the S-unit calibration is
                // chosen from it (#296), and a display string cannot be
                // measured against 30 MHz.
                session.FrequencyHz = _startFrequency;
                session.ModeText = _startMode;
                session.SliceLetter = _startSlice;
                session.RadioModelText = SafeRead(() => rig.RadioModel, "") ?? "";

                _session = session;
                _rig = rig;
                rig.MeterData += OnMeterData;

                _registration = RunningCostRegister.Register(
                    new RunningCost("qso-signal-capture", "QSO signal capture")
                    {
                        IsRunning = () => session.IsRunning,
                        DescribeCost = () => Lexicon.Get("logging.running.qso_capture",
                            ("duration", SpokenDuration.English(session.ElapsedSeconds)),
                            ("count", session.SampleCount.ToString("N0", CultureInfo.CurrentCulture))),
                        Measure = () => (long)session.ElapsedSeconds,
                        Thresholds = new long[] { 15 * 60, 60 * 60 },
                        DescribeThreshold = v => SpokenDuration.English(v),
                        // No dispatcher marshalling needed: stopping touches the
                        // buffer, the store and the meter subscription — no UI
                        // objects — so the exit path may call it from any thread.
                        Stop = () => Stop("stopped from the exit prompt", out _),
                        StopHow = "press Control J, then Control Q",
                        SurvivesRestart = false,
                        Weight = RunningCostWeight.Notable,
                    });

                Tracing.TraceLine("QsoSignalCapture: started " + session.CaptureId
                    + " on slice " + (_startSlice.Length > 0 ? _startSlice : "?")
                    + " at " + session.FrequencyText, TraceLevel.Info);
                return true;
            }
        }

        /// <summary>
        /// Stop the live capture: analyze, bake, persist, unregister. Null
        /// when nothing was running. <paramref name="saved"/> reports whether
        /// the record reached disk — the caller must say so either way,
        /// because a capture the operator believes saved and is not would
        /// surface a week later as a missing artifact.
        /// </summary>
        public static QsoSignalCaptureStopResult? Stop(string endReason, out bool saved)
        {
            QsoSignalCaptureSession? session;
            lock (Gate)
            {
                session = _session;
                if (session == null) { saved = false; return null; }

                if (_rig != null) _rig.MeterData -= OnMeterData;
                _rig = null;
                _session = null;
                _registration?.Dispose();
                _registration = null;
            }

            QsoSignalCaptureStopResult result = session.Stop(endReason);
            saved = QsoSignalCaptureStore.Default().Save(result.Record);
            Tracing.TraceLine("QsoSignalCapture: stopped " + result.Record.CaptureId
                + " after " + result.Record.CaptureSeconds.ToString("0", CultureInfo.InvariantCulture)
                + " s, " + result.Analysis.SampleCount.ToString(CultureInfo.InvariantCulture)
                + " samples, saved=" + saved, TraceLevel.Info);
            return result;
        }

        // -------- the tap --------

        private static void OnMeterData(object? sender, Meter meter, float value)
        {
            // FlexLib's meter thread: cheap, non-blocking, and never throwing —
            // a capture must not be able to take the meter pump down.
            try
            {
                QsoSignalCaptureSession? session = _session;
                FlexBase? rig = _rig;
                if (session == null || rig == null || meter == null) return;

                if (!string.Equals(meter.Name, "LEVEL", StringComparison.OrdinalIgnoreCase))
                    return;
                if (!string.Equals(meter.Source, Meter.SOURCE_SLICE, StringComparison.OrdinalIgnoreCase))
                    return;

                string sliceLetter = SafeRead(() => rig.ActiveSliceLetter, "") ?? "";
                int active = SliceIndexOf(sliceLetter);
                if (active < 0 || meter.SourceIndex != active) return;

                bool transmitting = rig.Transmit || SafeRead(() => rig.TxTune, false);
                session.Add(value, transmitting);

                // Contamination watch: latch, never clear.
                if (_startSlice.Length > 0 && sliceLetter.Length > 0
                    && !string.Equals(sliceLetter, _startSlice, StringComparison.OrdinalIgnoreCase))
                    session.SliceChanged = true;
                ulong freq = SafeRead(() => rig.RXFrequency, _startFrequency);
                if (_startFrequency > 0 && freq > 0 && freq != _startFrequency)
                    session.FrequencyChanged = true;
                string mode = SafeRead(() => rig.Mode, _startMode) ?? _startMode;
                if (_startMode.Length > 0 && mode.Length > 0
                    && !string.Equals(mode, _startMode, StringComparison.OrdinalIgnoreCase))
                    session.ModeChanged = true;
            }
            catch
            {
                // Swallowed by design; the handler contract on MeterData is
                // "cheap and must not block", and a diagnostic here would
                // itself run at meter rate.
            }
        }

        private static int SliceIndexOf(string letter)
        {
            if (string.IsNullOrEmpty(letter)) return -1;
            char c = char.ToUpperInvariant(letter[0]);
            return (c >= 'A' && c <= 'H') ? c - 'A' : -1;
        }

        private static string FormatMHz(ulong hz)
            => (hz / 1_000_000.0).ToString("0.000###", CultureInfo.InvariantCulture) + " MHz";

        private static T SafeRead<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch { return fallback; }
        }
    }
}
