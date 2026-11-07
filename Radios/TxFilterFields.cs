using System;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// The two transmit filter fields of one surface (Home, or the Audio
    /// Workshop), and the ONE speaker for them (#747, round nine of the
    /// 2026-11-06 junk box).
    /// </summary>
    /// <remarks>
    /// <para><b>Why one speaker.</b> A value field said the number it ASKED
    /// for, on its own key, and a separate correction then said what FlexLib
    /// set. Two streams, and each round's patch to keep them in step opened a
    /// new way for the last word to be false: a refusal said before the
    /// field's number, a typed value that did not replace an old correction,
    /// a request for one edge that silenced the other edge's correction, a
    /// whole-pair correction outliving a true change to the other edge
    /// (Codex's fifth re-verify). So the fields say nothing of their own
    /// (<c>ValueFieldControl.SpeaksOnlyTheAppliedResult</c>), and the only
    /// speech is the applied result, from this class, after the write ran.</para>
    /// <para><b>One key per surface.</b> Both fields speak on
    /// <paramref name="surfaceKey"/> with Latest semantics, so a newer result
    /// for EITHER edge replaces an older one still waiting. Each result states
    /// what is true when it is made: normally just its own field ("TX Filter
    /// Low 2750"), marked ", at limit" when FlexLib held it back, and the whole
    /// pair if the other edge moved too.</para>
    /// <para><b>Request identity.</b> Each request records its field, its
    /// number within that field, the surface's epoch, and the rig, Radio and
    /// command connection it was made on. A completion speaks only if it
    /// answers that field's newest request, the epoch is unchanged
    /// (<see cref="Invalidate"/> on Initialize, Detach, SetRig or close), the
    /// same connection is still the radio's, and the surface is open.</para>
    /// </remarks>
    public sealed class TxFilterFields
    {
        private readonly string _surfaceKey, _lowLabel, _highLabel;
        private readonly Func<FlexBase?> _rig;
        private readonly Func<bool> _open;
        private readonly Action<string, string> _latest;
        private readonly Action<FilterApplied>? _refresh;
        private readonly object _gate = new object();
        private long _epoch, _lowNewest, _highNewest;

        /// <param name="surfaceKey">The one Latest key both fields speak on (a <c>SpeechSubject.ValueField</c> name for the surface).</param>
        /// <param name="lowLabel">The low field's label, said with its value.</param>
        /// <param name="highLabel">The high field's label.</param>
        /// <param name="rig">The surface's rig now.</param>
        /// <param name="open">Whether the surface is still open; asked on the command loop, so it must be thread-safe.</param>
        /// <param name="latest">The speaking call, (text, key); null speaks through <see cref="ScreenReaderOutput"/> on Latest.</param>
        /// <param name="refresh">Put the fields on the applied values (the host marshals to its window).</param>
        public TxFilterFields(string surfaceKey, string lowLabel, string highLabel, Func<FlexBase?> rig, Func<bool> open,
            Action<string, string>? latest = null, Action<FilterApplied>? refresh = null)
        {
            _surfaceKey = surfaceKey;
            _lowLabel = lowLabel;
            _highLabel = highLabel;
            _rig = rig;
            _open = open;
            _latest = latest ?? SpeakLatest;
            _refresh = refresh;
        }

        private static void SpeakLatest(string text, string key)
            => ScreenReaderOutput.Speak(text, Speech.SpeechIntent.Latest, VerbosityLevel.Terse,
                coalesceKey: key, kind: Speech.SpeechCoalesceKind.Value);

        /// <summary>Every request in flight stops being an answer: Initialize, Detach, SetRig, close.</summary>
        public void Invalidate()
        {
            lock (_gate) _epoch++;
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
            long epoch, request;
            lock (_gate)
            {
                epoch = _epoch;
                request = lowField ? ++_lowNewest : ++_highNewest;
            }
            string label = lowField ? _lowLabel : _highLabel;
            rig.SetTXFilter(lowField ? value : (int?)null, lowField ? (int?)null : value, applied =>
            {
                if (!Answers(rig, radio, connection, epoch, lowField, request)) return;
                _latest(Result(applied, lowField, label), _surfaceKey);
                _refresh?.Invoke(applied);
            });
        }

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

        /// <summary>
        /// What one field's write says, from what FlexLib set. DRAFT.
        /// <list type="bullet">
        /// <item>Not written: "TX filter not changed. The radio is not taking changes right now."</item>
        /// <item>The other edge moved too: the whole pair, "TX filter 2700 to 2900", with ", at limit" if not as asked.</item>
        /// <item>Otherwise its own field: "TX Filter Low 2750", with ", at limit" if FlexLib held it back.</item>
        /// </list>
        /// </summary>
        public static string Result(FilterApplied applied, bool lowField, string label)
        {
            if (applied.NotWritten) return Lexicon.Get("audio.tx_filter.not_written");
            string atLimit = applied.Exact ? "" : Lexicon.Get("settings.filter.suffix_at_limit");
            bool otherMoved = lowField ? applied.High != applied.BeforeHigh : applied.Low != applied.BeforeLow;
            if (otherMoved)
                return Lexicon.Get("audio.tx_filter.range", ("low", applied.Low), ("high", applied.High)) + atLimit;
            int value = lowField ? applied.Low : applied.High;
            return Lexicon.Get("audio.field.spoken_value", ("label", label), ("value", value), ("unit", "")) + atLimit;
        }
    }
}
