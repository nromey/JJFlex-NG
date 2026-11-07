using System;
using System.Collections.Generic;
using System.Linq;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// The two transmit filter fields of one surface (Home, or the Audio
    /// Workshop), and the ONE speaker for them (#747, rounds nine and eleven
    /// of the 2026-11-06 junk box). The state table this implements is in the
    /// round-eleven section of the batch's rulings report.
    /// </summary>
    /// <remarks>
    /// <para><b>Why one speaker.</b> A value field said the number it ASKED
    /// for, on its own key, and a separate correction then said what FlexLib
    /// set; each round's patch to keep two streams in step opened a new way for
    /// the last word to be false (Codex's fifth re-verify). So the fields say
    /// nothing of their own (<c>ValueFieldControl.SpeaksOnlyTheAppliedResult</c>)
    /// and every word about them comes from here: the applied result after a
    /// write, and the end stop, which says the APPLIED value (round eleven).</para>
    /// <para><b>One key per surface, and no erasure.</b> Both fields speak on
    /// one Latest key, so a newer result for either edge replaces an older one
    /// still waiting. A replacement must not lose what it replaced (Codex's
    /// sixth re-verify), so each sentence states every edge that differs from
    /// what the operator last HEARD. A sentence counts as heard once no newer
    /// one followed it within the arbiter's longest possible wait; until then
    /// the next sentence covers it too. Over-covering is a repeat; under-
    /// covering would be a lost edge.</para>
    /// <para><b>Request identity.</b> Each request records its field, its
    /// number within that field, the surface's epoch, and the rig, Radio and
    /// command connection it was made on. A completion speaks only if it
    /// answers that field's newest request, in the same epoch, on the same
    /// connection, with the surface open; a window refresh re-checks the same
    /// when it RUNS. <see cref="Invalidate"/> (Initialize, Detach, SetRig,
    /// close) and a change of the Radio's command connection move the epoch on
    /// AND retire a result still waiting on the key, unheard.</para>
    /// </remarks>
    public sealed class TxFilterFields
    {
        private readonly string _surfaceKey, _lowLabel, _highLabel;
        private readonly Func<FlexBase?> _rig;
        private readonly Func<bool> _open;
        private readonly Action<string, string> _latest;
        private readonly Action<FilterApplied, Func<bool>>? _refresh;
        private readonly Func<DateTime> _now;
        private readonly Action<string> _retire;
        private readonly object _gate = new object();
        private long _epoch, _lowNewest, _highNewest;

        // What has been said: the pair each submitted sentence left the
        // operator believing, when, and how long it could have waited unheard.
        private readonly List<(DateTime At, int Low, int High, int MayWaitMs)> _said = new();
        private (int Low, int High)? _baseline;
        private bool _lowAtLimit, _highAtLimit;

        private Radio? _watchedRadio;

        /// <param name="surfaceKey">The one Latest key both fields speak on.</param>
        /// <param name="lowLabel">The low field's label, said with its value.</param>
        /// <param name="highLabel">The high field's label.</param>
        /// <param name="rig">The surface's rig now.</param>
        /// <param name="open">Whether the surface is still open; asked on the command loop, so it must be thread-safe.</param>
        /// <param name="latest">The speaking call, (text, key); null speaks through <see cref="ScreenReaderOutput"/> on Latest.</param>
        /// <param name="refresh">Put the fields on the applied values. The second argument says whether this result still answers; call it again inside the queued window work, when it runs.</param>
        /// <param name="now">The speech clock; null is the wall clock.</param>
        /// <param name="retire">Withdraw a result still waiting on the key; null uses <see cref="ScreenReaderOutput.RetireLatest"/>.</param>
        public TxFilterFields(string surfaceKey, string lowLabel, string highLabel, Func<FlexBase?> rig, Func<bool> open,
            Action<string, string>? latest = null, Action<FilterApplied, Func<bool>>? refresh = null,
            Func<DateTime>? now = null, Action<string>? retire = null)
        {
            _surfaceKey = surfaceKey;
            _lowLabel = lowLabel;
            _highLabel = highLabel;
            _rig = rig;
            _open = open;
            _latest = latest ?? SpeakLatest;
            _refresh = refresh;
            _now = now ?? (() => DateTime.UtcNow);
            _retire = retire ?? (key => ScreenReaderOutput.RetireLatest(key));
        }

        private static void SpeakLatest(string text, string key)
            => ScreenReaderOutput.Speak(text, Speech.SpeechIntent.Latest, VerbosityLevel.Terse,
                coalesceKey: key, kind: Speech.SpeechCoalesceKind.Value);

        /// <summary>
        /// The surface's context ended (Initialize, Detach, SetRig, close, or a
        /// connection change): nothing in flight answers any more, and a result
        /// still waiting to be said is withdrawn, unheard.
        /// </summary>
        public void Invalidate()
        {
            lock (_gate)
            {
                _epoch++;
                _said.Clear();
                _baseline = null;
            }
            _retire(_surfaceKey);
        }

        /// <summary>
        /// A field moved to <paramref name="value"/>: by an arrow, a held key or
        /// typed entry, all alike. Writes that edge through the applied route
        /// and says the result.
        /// </summary>
        public void Write(bool lowField, int value)
        {
            var rig = _rig();
            if (rig == null) return;
            Radio? radio = rig.theRadio;
            object? connection = radio?.CurrentCommandConnection;
            Watch(radio);
            long epoch, request;
            lock (_gate)
            {
                epoch = _epoch;
                request = lowField ? ++_lowNewest : ++_highNewest;
            }
            string label = lowField ? _lowLabel : _highLabel;
            rig.SetTXFilter(lowField ? value : (int?)null, lowField ? (int?)null : value, applied =>
            {
                bool StillCurrent() => Answers(rig, radio, connection, epoch, lowField, request);
                if (!StillCurrent()) return;
                Say(Result(applied, lowField, label));
                _refresh?.Invoke(applied, StillCurrent);
            });
        }

        /// <summary>
        /// A press against a field's end (round eleven, Codex's end-stop
        /// blocker): the boundary word with the APPLIED value of that edge,
        /// read from the radio, never the field's own optimistic value.
        /// <paramref name="shownValue"/> is used only when there is no radio
        /// to read. DRAFT.
        /// </summary>
        public void SayEndStop(bool lowField, bool atMinimum, int shownValue)
        {
            var rig = _rig();
            int value = rig?.theRadio == null ? shownValue : (lowField ? rig.TXFilterLow : rig.TXFilterHigh);
            string label = lowField ? _lowLabel : _highLabel;
            string text = Lexicon.Get(atMinimum ? "audio.field.spoken_value_minimum" : "audio.field.spoken_value_maximum",
                ("label", label), ("value", value), ("unit", ""));
            if (rig?.theRadio != null)
            {
                lock (_gate) Record(rig.TXFilterLow, rig.TXFilterHigh, text);
            }
            _latest(text, _surfaceKey);
        }

        /// <summary>Follow the Radio's command connection: a change retires what is waiting.</summary>
        private void Watch(Radio? radio)
        {
            lock (_gate)
            {
                if (ReferenceEquals(radio, _watchedRadio)) return;
                if (_watchedRadio != null) _watchedRadio.CommandConnectionChanged -= OnConnectionChanged;
                _watchedRadio = radio;
                if (radio != null) radio.CommandConnectionChanged += OnConnectionChanged;
            }
        }

        private void OnConnectionChanged(CommandConnectionChanged report) => Invalidate();

        private bool Answers(FlexBase rig, Radio? radio, object? connection, long epoch, bool lowField, long request)
        {
            lock (_gate)
            {
                if (epoch != _epoch) return false;
                if (request != (lowField ? _lowNewest : _highNewest)) return false;
            }
            return ReferenceEquals(rig, _rig())
                && ReferenceEquals(rig.theRadio, radio)
                && ReferenceEquals(radio?.CurrentCommandConnection, connection)
                && _open();
        }

        private void Say(string text) => _latest(text, _surfaceKey);

        /// <summary>
        /// What one field's write says, from what FlexLib set, covering every
        /// edge that differs from what was last heard. DRAFT.
        /// </summary>
        private string Result(FilterApplied applied, bool lowField, string label)
        {
            lock (_gate)
            {
                var heard = HeardPairLocked(applied);
                int low = applied.Low, high = applied.High;
                if (applied.NotWritten && _said.Count > 0)
                {
                    // A refusal changes nothing; the pair is the last one said.
                    low = _said[_said.Count - 1].Low;
                    high = _said[_said.Count - 1].High;
                }
                if (!applied.NotWritten)
                {
                    if (lowField) _lowAtLimit = applied.Low != applied.RequestedLow;
                    else _highAtLimit = applied.High != applied.RequestedHigh;
                }

                var clauses = new List<string>();
                bool lowSays = low != heard.Low || (!applied.NotWritten && lowField);
                bool highSays = high != heard.High || (!applied.NotWritten && !lowField);
                if (lowSays) clauses.Add(Clause(_lowLabel, low, _lowAtLimit));
                if (highSays) clauses.Add(Clause(_highLabel, high, _highAtLimit));
                if (applied.NotWritten) clauses.Add(Lexicon.Get("audio.tx_filter.not_written"));
                string text = string.Join(Lexicon.Get("audio.tx_filter.clause_joiner"), clauses);
                Record(low, high, text);
                return text;
            }
        }

        private static string Clause(string label, int value, bool atLimit)
            => Lexicon.Get("audio.field.spoken_value", ("label", label), ("value", value), ("unit", ""))
               + (atLimit ? Lexicon.Get("settings.filter.suffix_at_limit") : "");

        /// <summary>
        /// The pair the operator last HEARD: the newest sentence that no newer
        /// sentence followed within its possible wait, else the pair before the
        /// first one.
        /// </summary>
        private (int Low, int High) HeardPairLocked(FilterApplied applied)
        {
            _baseline ??= (applied.BeforeLow, applied.BeforeHigh);
            var heard = _baseline.Value;
            DateTime now = _now();
            for (int i = 0; i < _said.Count; i++)
            {
                DateTime next = i + 1 < _said.Count ? _said[i + 1].At : now;
                if ((next - _said[i].At).TotalMilliseconds >= _said[i].MayWaitMs)
                    heard = (_said[i].Low, _said[i].High);
            }
            return heard;
        }

        private void Record(int low, int high, string text)
        {
            DateTime now = _now();
            // Forget sentences that are certainly heard, keeping the newest of
            // them as the baseline, so the list stays short.
            var heard = _baseline ?? (low, high);
            while (_said.Count > 0 && (now - _said[0].At).TotalMilliseconds >= _said[0].MayWaitMs
                   && (_said.Count == 1 || (_said[1].At - _said[0].At).TotalMilliseconds >= _said[0].MayWaitMs))
            {
                heard = (_said[0].Low, _said[0].High);
                _said.RemoveAt(0);
            }
            _baseline = heard;
            // The longest a Latest value can wait unheard: a sweep's settle,
            // plus the anti-clip gap after whatever was said before it.
            int mayWait = Speech.SpeechArbiter.SweepWindowMs + Speech.SpeechArbiter.CoalesceMs
                + Speech.SpeechArbiter.AntiClipGapMs(text);
            _said.Add((now, low, high, mayWait));
            // A long unbroken sweep keeps every entry unheard; past this many,
            // the oldest is dropped (its edges are still covered by the newer
            // ones, which hold the same or later values).
            if (_said.Count > 64) _said.RemoveAt(0);
        }
    }
}
