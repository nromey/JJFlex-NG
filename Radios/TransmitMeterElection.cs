using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Radios
{
    /// <summary>
    /// Which copy of a duplicated transmit-chain meter to believe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The radio publishes SC_MIC, ALC and the rest of the transmit chain
    /// <b>once per transmit source</b>, and nothing in the descriptors says
    /// which source is carrying the operator's audio. Measured: the bench
    /// FLEX-8600 with a station client and four slices open published four
    /// SC_MIC meters (indices 24, 48, 72, 91), byte-identical, all claiming
    /// source <c>TX-</c> index 0. Don's FLEX-6300 over SmartLink publishes
    /// three — <c>[17] TX-:8</c>, <c>[21] TX-:8</c>, <c>[43] TX-:9</c> (#502).
    /// So the source index is not a slice number and cannot be the key; on
    /// one radio it does not vary at all.
    /// </para>
    /// <para>
    /// FlexLib's <c>Radio.FindMeterByName</c> is a <c>FirstOrDefault</c>, and
    /// <c>MeterInventory.Find</c> is first-name-wins. Both hand back whichever
    /// copy the radio listed first. On the 8600 that happened to be the copy
    /// that streams; on Don's radio it was a copy that <b>never delivered a
    /// sample</b>, so the mic level sat at its -150 floor through two
    /// transmissions while the transmit monitor proved audio was arriving, and
    /// the operator was told no transmit audio was reaching the radio.
    /// <c>MeterInventory</c> predicted this in its own remarks on 2026-08-20
    /// and declined to fix it because no rule for choosing had been
    /// established. This class is that rule.
    /// </para>
    /// <para>
    /// <b>The rule: the copy that streams during a transmission is the one to
    /// believe, and nothing is believed until a copy has reported.</b>
    /// </para>
    /// <list type="number">
    /// <item><description>No election until a copy reports. A copy that has
    /// never delivered a sample cannot be chosen, whatever its position in
    /// the list — which on its own excludes the copy that fooled us.</description></item>
    /// <item><description>The first copy to report is elected provisionally.</description></item>
    /// <item><description>Another copy displaces it when it carries more signal
    /// since the last key-down, provided the elected copy has either reported
    /// since that key-down too (so two live readings are being compared) or
    /// has been quiet for <see cref="QuietMs"/> (so it is not merely a packet
    /// behind). Signal outranks liveness: a copy that carried real audio and
    /// then went quiet is not displaced by one streaming the floor.</description></item>
    /// <item><description>Every election is explained, in the trace and in
    /// <see cref="Describe"/>, naming every copy and what it has done. When
    /// copies cannot be told apart the text says so rather than pretending
    /// the choice was principled.</description></item>
    /// </list>
    /// <para>
    /// Why this is sound under MultiFlex: transmit is a mutex on the radio,
    /// so while THIS client is keyed the copy that rises above the floor is
    /// carrying this client's audio. The same rule the transmit antenna needs
    /// (#496) — the transmit chain's instance, not the first instance found —
    /// resolved by observation because the descriptors cannot resolve it.
    /// </para>
    /// <para>
    /// Pure and clock-free: the caller passes a tick, so the rule is testable
    /// against constructed inventories, which is the only way the multi-copy
    /// case can be verified from a machine with a single-source radio on the
    /// bench. Every public member takes one uncontended lock; samples arrive on
    /// FlexLib's meter thread and are read from wherever a consumer happens
    /// to be.
    /// </para>
    /// </remarks>
    public sealed class TransmitMeterElection
    {
        /// <summary>How long the elected copy may go without a sample before
        /// another reporting copy may displace it on liveness alone. Meters
        /// stream at tens of samples a second, so a full second of nothing is
        /// a copy that has stopped, not a copy that is a packet behind.</summary>
        public const int QuietMs = 1000;

        /// <summary>One copy of the meter, and what it has done so far.</summary>
        public sealed class Candidate
        {
            internal Candidate(object key, string label, int index)
            {
                Key = key;
                Label = label ?? "";
                Index = index;
            }

            internal object Key { get; }

            /// <summary>How the copy is named in traces — the same form the
            /// meterInventory lines use, e.g. <c>[43] TX-:9</c>, so a reader
            /// can match the two.</summary>
            public string Label { get; }

            /// <summary>The radio's meter index for this copy.</summary>
            public int Index { get; }

            /// <summary>Samples ever delivered.</summary>
            public long Samples { get; internal set; }

            /// <summary>Samples delivered since the last <see cref="ResetPeaks"/>
            /// — since key-down, in practice.</summary>
            public int SamplesSinceReset { get; internal set; }

            /// <summary>Highest value since the last reset, or NaN when this
            /// copy has not reported since then.</summary>
            public float PeakSinceReset { get; internal set; } = float.NaN;

            /// <summary>The last value delivered, or NaN when none ever was.</summary>
            public float LastValue { get; internal set; } = float.NaN;

            /// <summary>Tick of the last sample, in the caller's clock.</summary>
            public int LastTick { get; internal set; }

            /// <summary>True once this copy has delivered at least one sample.</summary>
            public bool HasReported => Samples > 0;

            internal float PeakOrNegativeInfinity =>
                float.IsNaN(PeakSinceReset) ? float.NegativeInfinity : PeakSinceReset;
        }

        /// <summary>What a reported sample did to the election.</summary>
        public enum Outcome
        {
            /// <summary>The key is not a registered copy. Register it and report again.</summary>
            Unknown,
            /// <summary>The sample came from a copy that is not the elected one
            /// and did not change the election. Not to be published.</summary>
            Ignored,
            /// <summary>The sample came from the elected copy. Publish it.</summary>
            Accepted,
            /// <summary>Nothing was elected and this copy is now. Publish it.</summary>
            Elected,
            /// <summary>This copy displaced the previously elected one. Publish it.</summary>
            Displaced
        }

        private readonly object _lock = new object();
        private readonly Dictionary<object, Candidate> _byKey = new Dictionary<object, Candidate>();
        private readonly List<Candidate> _inOrder = new List<Candidate>();
        private Candidate _elected;
        private string _lastElectionReason = "";

        public TransmitMeterElection(string meterName)
        {
            MeterName = meterName ?? "";
        }

        /// <summary>The meter name every candidate here is a copy of.</summary>
        public string MeterName { get; }

        /// <summary>How many copies the radio currently publishes.</summary>
        public int CandidateCount { get { lock (_lock) return _inOrder.Count; } }

        /// <summary>Every copy, in registration order.</summary>
        public IReadOnlyList<Candidate> Candidates
        {
            get { lock (_lock) return _inOrder.ToArray(); }
        }

        /// <summary>The copy currently believed, or null when no copy has ever reported.</summary>
        public Candidate Elected { get { lock (_lock) return _elected; } }

        /// <summary>True once some copy has reported and so been elected.</summary>
        public bool HasElected { get { lock (_lock) return _elected != null; } }

        /// <summary>True when the elected copy has delivered a sample since the
        /// last <see cref="ResetPeaks"/>. <b>This is the telemetry test a floor
        /// reading must pass before it means anything</b> (#459, #502): a
        /// value of the floor from a copy that has said nothing this
        /// transmission is not a measurement of silence, it is the absence of
        /// a measurement.</summary>
        public bool ElectedReportedSinceReset
        {
            get { lock (_lock) return _elected != null && _elected.SamplesSinceReset > 0; }
        }

        /// <summary>The elected copy's last value, or NaN when nothing is elected.</summary>
        public float ElectedLast { get { lock (_lock) return _elected?.LastValue ?? float.NaN; } }

        /// <summary>The elected copy's peak since the last reset, or NaN when
        /// nothing is elected or it has not reported since.</summary>
        public float ElectedPeakSinceReset { get { lock (_lock) return _elected?.PeakSinceReset ?? float.NaN; } }

        /// <summary>Why the current copy is the elected one, in words.</summary>
        public string LastElectionReason { get { lock (_lock) return _lastElectionReason; } }

        /// <summary>
        /// Make a copy known. Idempotent. <paramref name="key"/> is the copy's
        /// identity — the FlexLib Meter object itself in production, since a
        /// removed-then-re-added meter is a new object that may reuse its index.
        /// </summary>
        public Candidate Register(object key, string label, int index)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            lock (_lock)
            {
                if (_byKey.TryGetValue(key, out Candidate c)) return c;
                c = new Candidate(key, label, index);
                _byKey[key] = c;
                _inOrder.Add(c);
                return c;
            }
        }

        /// <summary>
        /// Forget every copy <paramref name="stillPresent"/> rejects. If the
        /// elected copy goes, nothing is elected until a remaining copy reports
        /// — the meter object is gone, and a new one will introduce itself.
        /// </summary>
        public void KeepOnly(Func<object, bool> stillPresent)
        {
            if (stillPresent == null) throw new ArgumentNullException(nameof(stillPresent));
            lock (_lock)
            {
                for (int i = _inOrder.Count - 1; i >= 0; i--)
                {
                    Candidate c = _inOrder[i];
                    if (stillPresent(c.Key)) continue;
                    _inOrder.RemoveAt(i);
                    _byKey.Remove(c.Key);
                    if (ReferenceEquals(c, _elected))
                    {
                        _elected = null;
                        _lastElectionReason = "the elected copy " + c.Label + " was withdrawn by the radio";
                    }
                }
            }
        }

        /// <summary>Forget everything. The radio went away.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _inOrder.Clear();
                _byKey.Clear();
                _elected = null;
                _lastElectionReason = "";
            }
        }

        /// <summary>
        /// A new transmission is starting: every copy's peak and since-reset
        /// count start over. The election itself is kept — a proven choice
        /// stays until another copy earns the place.
        /// </summary>
        public void ResetPeaks()
        {
            lock (_lock)
            {
                foreach (Candidate c in _inOrder)
                {
                    c.SamplesSinceReset = 0;
                    c.PeakSinceReset = float.NaN;
                }
            }
        }

        /// <summary>
        /// A copy delivered a sample. Records it against that copy, applies the
        /// rule, and says whether the value is the one to publish.
        /// </summary>
        /// <param name="key">The copy's identity, as registered.</param>
        /// <param name="value">The sample.</param>
        /// <param name="nowTick">The caller's clock, in milliseconds
        /// (<c>Environment.TickCount</c> in production).</param>
        public Outcome Report(object key, float value, int nowTick)
        {
            if (key == null) return Outcome.Unknown;
            lock (_lock)
            {
                if (!_byKey.TryGetValue(key, out Candidate c)) return Outcome.Unknown;

                c.Samples++;
                c.SamplesSinceReset++;
                c.LastValue = value;
                c.LastTick = nowTick;
                if (float.IsNaN(c.PeakSinceReset) || value > c.PeakSinceReset) c.PeakSinceReset = value;

                if (_elected == null)
                {
                    _elected = c;
                    _lastElectionReason = _inOrder.Count == 1
                        ? "the only copy the radio publishes"
                        : "the first of " + _inOrder.Count.ToString(CultureInfo.InvariantCulture)
                          + " copies to report, provisional until another copy carries more signal";
                    return Outcome.Elected;
                }

                if (ReferenceEquals(c, _elected)) return Outcome.Accepted;

                Candidate e = _elected;
                if (!Displaces(c, e, nowTick)) return Outcome.Ignored;

                _lastElectionReason = e.SamplesSinceReset > 0
                    ? "reported " + Db(c.PeakSinceReset) + " since key-down while " + e.Label
                      + " reported " + Db(e.PeakSinceReset)
                    : e.Label + " has been quiet for "
                      + (unchecked(nowTick - e.LastTick) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                      + " s while this copy is streaming";
                _elected = c;
                return Outcome.Displaced;
            }
        }

        /// <summary>
        /// Rule 3. Signal first, then liveness — and never on a single packet's
        /// head start: right after a reset every copy's peak is NaN, so the
        /// first packet through would otherwise flip the election by arrival
        /// order on every key-down, which is precisely the implicit ordering
        /// this class exists to replace.
        /// </summary>
        private static bool Displaces(Candidate challenger, Candidate elected, int nowTick)
        {
            if (!(challenger.PeakOrNegativeInfinity > elected.PeakOrNegativeInfinity)) return false;
            bool electedLiveThisWindow = elected.SamplesSinceReset > 0;
            bool electedQuiet = unchecked(nowTick - elected.LastTick) >= QuietMs;
            return electedLiveThisWindow || electedQuiet;
        }

        /// <summary>
        /// One line a person can read: how many copies, which is elected and
        /// why, and what every other copy has done. When nothing is elected it
        /// says so, in the terms that matter — that a floor reading from here
        /// would be a fabrication.
        /// </summary>
        public string Describe(int nowTick)
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.Append(MeterName).Append(": ");
                if (_inOrder.Count == 0)
                {
                    sb.Append("no copies published by the radio");
                    return sb.ToString();
                }
                sb.Append(_inOrder.Count.ToString(CultureInfo.InvariantCulture))
                  .Append(_inOrder.Count == 1 ? " copy" : " copies");
                if (_elected == null)
                {
                    sb.Append(", none has reported; nothing elected, so a floor reading from here would be a fabrication, not a measurement");
                    if (_lastElectionReason.Length != 0) sb.Append(" (").Append(_lastElectionReason).Append(')');
                    AppendOthers(sb, nowTick, null);
                    return sb.ToString();
                }
                sb.Append(". Elected ").Append(_elected.Label)
                  .Append(" — ").Append(_lastElectionReason).Append("; ")
                  .Append(Samples(_elected.SamplesSinceReset))
                  .Append(" since key-down, peak ").Append(Db(_elected.PeakSinceReset))
                  .Append(", last ").Append(Db(_elected.LastValue));
                AppendOthers(sb, nowTick, _elected);
                return sb.ToString();
            }
        }

        private void AppendOthers(StringBuilder sb, int nowTick, Candidate except)
        {
            bool any = false;
            foreach (Candidate c in _inOrder)
            {
                if (ReferenceEquals(c, except)) continue;
                sb.Append(any ? "; " : ". Others: ");
                any = true;
                sb.Append(c.Label).Append(' ');
                if (!c.HasReported) { sb.Append("never reported"); continue; }
                if (c.SamplesSinceReset == 0)
                {
                    sb.Append("no samples since key-down, last reported ")
                      .Append((unchecked(nowTick - c.LastTick) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture))
                      .Append(" s ago at ").Append(Db(c.LastValue));
                    continue;
                }
                sb.Append(Samples(c.SamplesSinceReset))
                  .Append(" since key-down, peak ").Append(Db(c.PeakSinceReset));
            }
        }

        private static string Samples(int n) =>
            n == 1 ? "1 sample" : n.ToString(CultureInfo.InvariantCulture) + " samples";

        /// <summary>A short census for the once-per-change trace: count and labels only.</summary>
        public string Census()
        {
            lock (_lock)
            {
                if (_inOrder.Count == 0) return MeterName + " NOT FOUND";
                var sb = new StringBuilder();
                sb.Append(MeterName).Append(' ')
                  .Append(_inOrder.Count.ToString(CultureInfo.InvariantCulture))
                  .Append(_inOrder.Count == 1 ? " copy (" : " copies (");
                for (int i = 0; i < _inOrder.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(_inOrder[i].Label);
                }
                sb.Append(')');
                if (_inOrder.Count > 1)
                    sb.Append(" — identical descriptors; the copy that streams while keyed will be believed, and the choice is traced");
                return sb.ToString();
            }
        }

        private static string Db(float v) =>
            float.IsNaN(v) ? "none" : v.ToString("F1", CultureInfo.InvariantCulture);
    }
}
