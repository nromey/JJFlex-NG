#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radios.SignalCapture
{
    /// <summary>
    /// One completed QSO signal capture, as it lives on disk: the readings,
    /// the context they were taken in, and the report both of its forms baked
    /// in at the moment the capture stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only completed captures are ever recorded (#271, ruled by Noel
    /// 2026-08-26).</b> A running capture has no file, is never resumed, and
    /// is never added to after it ends: a stored report is a record of a
    /// window that genuinely happened, and a resumed measurement would be a
    /// lie about when it was taken.
    /// </para>
    /// <para>
    /// The raw readings are stored alongside the baked report so the capture
    /// stays re-analyzable — per-station segmentation, when it comes, gets
    /// real captures to test against. Context fields hold display text, and
    /// <b>empty means could-not-be-read</b>, the Fixer fingerprint convention;
    /// the report names the absence rather than omitting the line.
    /// </para>
    /// <para>
    /// <see cref="Label"/> is the operator's own name for the capture — a
    /// capture is otherwise identified by nothing but an id and a timestamp,
    /// and "the one from 9:14" is useless a week later. The file path derives
    /// from the start stamp and id only, so relabelling and re-saving
    /// overwrites the same file in place.
    /// </para>
    /// </remarks>
    public sealed class QsoSignalCaptureRecord
    {
        public int Schema { get; set; } = 1;

        /// <summary>Speakable id from <see cref="Radios.Fixer.FixerRunId"/>,
        /// e.g. "A52-5T2" — same alphabet, same reasons.</summary>
        public string CaptureId { get; set; } = "";

        /// <summary>The operator's name for this capture. Empty until they
        /// rename it.</summary>
        public string Label { get; set; } = "";

        public DateTime StartedUtc { get; set; }
        public DateTime EndedUtc { get; set; }

        /// <summary>The whole operator window, start to stop, seconds.</summary>
        public double CaptureSeconds { get; set; }

        /// <summary>How the capture ended, as a phrase completing "It ran two
        /// minutes and was {EndReason}.": "stopped by you", "stopped from the
        /// exit prompt".</summary>
        public string EndReason { get; set; } = "";

        /// <summary>True when the buffer cap was reached and later readings
        /// were not kept.</summary>
        public bool BufferFilled { get; set; }

        // -------- context observations; empty means could-not-be-read --------

        public string FrequencyText { get; set; } = "";
        public string ModeText { get; set; } = "";
        public string SliceLetter { get; set; } = "";
        public string RadioModelText { get; set; } = "";

        public bool FrequencyChanged { get; set; }
        public bool ModeChanged { get; set; }
        public bool SliceChanged { get; set; }

        // -------- the readings --------

        /// <summary>Seconds since capture start, one per reading, ascending.</summary>
        public List<double> SampleOffsetsSeconds { get; set; } = new List<double>();

        /// <summary>The readings, dBm, parallel to the offsets.</summary>
        public List<double> SampleDbm { get; set; } = new List<double>();

        /// <summary>[start, end] second pairs during which this station was
        /// transmitting. Readings inside these windows are excluded from every
        /// statistic.</summary>
        public List<double[]> TransmitRanges { get; set; } = new List<double[]>();

        // -------- the baked report --------

        /// <summary>Peak as display text ("S9 plus 4 dB"), for the list row.
        /// Empty when nothing was measured.</summary>
        public string PeakDisplay { get; set; } = "";

        public string ReportText { get; set; } = "";
        public string ReportHtml { get; set; } = "";

        /// <summary>The name a surface shows: the operator's label when they
        /// gave one, the capture id otherwise.</summary>
        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Label) ? CaptureId : Label;

        /// <summary>One list row: name, start, length, peak — and an explicit
        /// "nothing measured" when there is no peak, because an absent
        /// measurement must never just be a missing clause.</summary>
        public string Summary()
        {
            string when = StartedUtc == default
                ? "start time unknown"
                : StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            string peak = string.IsNullOrWhiteSpace(PeakDisplay)
                ? "nothing measured"
                : "peaked " + PeakDisplay;
            return DisplayName + " — " + when + ", "
                 + SpokenDuration.English(CaptureSeconds) + ", " + peak;
        }

        /// <summary>The readings as analysis samples, transmit flags
        /// reconstructed from <see cref="TransmitRanges"/>.</summary>
        public IReadOnlyList<QsoSignalSample> ToSamples()
        {
            int n = Math.Min(SampleOffsetsSeconds.Count, SampleDbm.Count);
            var samples = new List<QsoSignalSample>(n);
            for (int i = 0; i < n; i++)
            {
                double t = SampleOffsetsSeconds[i];
                bool tx = false;
                foreach (double[] range in TransmitRanges)
                {
                    if (range != null && range.Length == 2 && t >= range[0] && t <= range[1])
                    {
                        tx = true;
                        break;
                    }
                }
                samples.Add(new QsoSignalSample(t, SampleDbm[i], tx));
            }
            return samples;
        }

        // -------- serialization --------

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        /// <summary>Null when the text is not a readable record — including a
        /// record from a future schema, which must be skipped, never guessed
        /// at.</summary>
        public static QsoSignalCaptureRecord? FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                QsoSignalCaptureRecord? r =
                    JsonSerializer.Deserialize<QsoSignalCaptureRecord>(json, JsonOptions);
                if (r == null || r.Schema > 1) return null;
                if (string.IsNullOrWhiteSpace(r.CaptureId)) return null;
                return r;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
