using System;
using System.Collections.Generic;
using Flex.Smoothlake.FlexLib;
using Radios.Speech;

namespace Radios
{
    /// <summary>
    /// The two transmit filter fields of one surface (Home, or the Audio
    /// Workshop), and the ONE speaker for them (#747, rounds nine, eleven and
    /// twelve of the 2026-11-06 junk box). The state table this implements is
    /// in the round-twelve section of the batch's rulings report.
    /// </summary>
    /// <remarks>
    /// <para><b>Why one speaker.</b> A value field said the number it ASKED
    /// for, and a separate correction then said what FlexLib set; each patch
    /// to keep two streams in step opened a new way for the last word to be
    /// false. So the fields say nothing of their own
    /// (<c>ValueFieldControl.SpeaksOnlyTheAppliedResult</c>) and every word
    /// about them comes from here.</para>
    /// <para><b>Composed when it is SPOKEN (round twelve).</b> A sentence
    /// built when a write completed could be false by the time the arbiter
    /// handed it over: another door may have written the filter, or a refusal
    /// rebuilt the pair from history. So this class records only what is
    /// OWED, and the sentence is composed at hand-over
    /// (<see cref="SpeechArbiter.LatestComposed"/>) from the radio's pair at
    /// that moment. History decides WHICH edges to state; the radio supplies
    /// every value.</para>
    /// <para><b>The words are as fresh as the numbers (round thirteen).</b>
    /// An end stop's "maximum" or "minimum" is said only if the edge is still
    /// at that limit when the sentence is composed, and a waiting refusal is
    /// dropped once another door has moved the filter. The outcome callback
    /// takes only this class's own gate.</para>
    /// <para><b>Owed until heard, or until the next sentence is handed over
    /// (J35, option (b), ruled by Noel 2026-11-07).</b> An edge is owed when
    /// its newest result has not been said, when its value differs from the
    /// last value a sentence is counted as having stated, or when an end stop
    /// on it is waiting. A refusal is owed until said. A sentence is paid,
    /// crediting only what its text stated and only the obligations it
    /// carried, in one of two ways: the reader reports it Completed, or the
    /// NEXT sentence on these fields is handed over. The next sentence is
    /// composed BEFORE the one ahead of it is paid, so whatever that one
    /// stated and is still owed (cut off, refused, or simply not reported) is
    /// said again in the next sentence, once, and is then counted heard. Only
    /// NVDA reports Completed; before this, on JAWS every later sentence
    /// stated both edges until the surface closed. Elapsed time is no evidence
    /// of hearing and is not used.</para>
    /// <para><b>The outcome callback acts only on Completed</b>, and only for
    /// a sentence not yet paid: the arbiter can deliver it late or out of
    /// order on a timer thread (#916), so the hand-over, which runs in order
    /// under the arbiter's lock, does the paying that matters.</para>
    /// <para><b>Request identity.</b> A completion is recorded only if it
    /// answers that field's newest request, in the same epoch, on the same
    /// rig, Radio and command connection, with the surface open; a window
    /// refresh re-checks the same when it RUNS and reads the pair then.
    /// <see cref="Invalidate"/> (Initialize, Detach, SetRig, close) and a
    /// change of the Radio's command connection clear what is owed and
    /// retire a sentence still waiting, unheard.</para>
    /// </remarks>
    public sealed class TxFilterFields
    {
        private readonly string _surfaceKey, _lowLabel, _highLabel;
        private readonly Func<FlexBase?> _rig;
        private readonly Func<bool> _open;
        private readonly Action<string, Func<SpeechComposition?>> _speak;
        private readonly Action<Func<bool>>? _refresh;
        private readonly Action<string> _retire;
        private readonly object _gate = new object();
        private long _epoch, _lowNewest, _highNewest, _seq;

        // What is owed, all under _gate. A sequence of 0 means nothing owed.
        private FlexBase? _stateRig;
        private Radio? _stateRadio;
        private object? _stateConnection;
        private int? _heardLow, _heardHigh;      // the last value a COMPLETED sentence stated, or the pair before the first request
        private long _lowAsked, _highAsked;      // an unsaid newest result for that edge
        private int? _lowLimitAt, _highLimitAt;  // the value that edge's newest result was held back to
        private long _notWritten;                // an unsaid refusal
        private (int Low, int High)? _refusedPair; // the radio's pair when it was refused; another door moving it retires the refusal
        private long _endStopSeq;                // an unsaid end stop
        private bool _endStopLow, _endStopAtMinimum;
        private int _endStopShown;
        private Spoken? _handedOver;             // the last sentence handed over and not yet paid

        /// <summary>What one handed-over sentence stated and which obligations
        /// it carried; paid once, by Completed or by the next hand-over.</summary>
        private sealed class Spoken
        {
            public long Epoch;
            public int? StatedLow, StatedHigh;
            public long PaidLow, PaidHigh, PaidEnd, PaidRefusal;
            public bool Paid;
        }

        private Radio? _watchedRadio;

        /// <param name="surfaceKey">The one Latest key both fields speak on.</param>
        /// <param name="lowLabel">The low field's label, said with its value.</param>
        /// <param name="highLabel">The high field's label.</param>
        /// <param name="rig">The surface's rig now.</param>
        /// <param name="open">Whether the surface is still open; asked on the command loop and at hand-over, so it must be thread-safe.</param>
        /// <param name="speak">Submit the surface's composer on its key, (key, composer); null uses <see cref="ScreenReaderOutput.SpeakComposed"/>.</param>
        /// <param name="refresh">Put the fields on the radio's pair. The argument says whether this surface's request still answers; call it, and read the pair, inside the queued window work when it runs.</param>
        /// <param name="retire">Withdraw a sentence still waiting on the key; null uses <see cref="ScreenReaderOutput.RetireLatest"/>.</param>
        public TxFilterFields(string surfaceKey, string lowLabel, string highLabel, Func<FlexBase?> rig, Func<bool> open,
            Action<string, Func<SpeechComposition?>>? speak = null, Action<Func<bool>>? refresh = null,
            Action<string>? retire = null)
        {
            _surfaceKey = surfaceKey;
            _lowLabel = lowLabel;
            _highLabel = highLabel;
            _rig = rig;
            _open = open;
            _speak = speak ?? ((key, compose) => ScreenReaderOutput.SpeakComposed(key, compose, VerbosityLevel.Terse));
            _refresh = refresh;
            _retire = retire ?? (key => ScreenReaderOutput.RetireLatest(key));
        }

        /// <summary>
        /// The surface's context ended (Initialize, Detach, SetRig, close, or a
        /// connection change): nothing in flight answers any more, nothing is
        /// owed, and a sentence still waiting to be said is withdrawn, unheard.
        /// </summary>
        public void Invalidate()
        {
            lock (_gate)
            {
                _epoch++;
                ClearOwedLocked();
                _stateRig = null;
                _stateRadio = null;
                _stateConnection = null;
            }
            _retire(_surfaceKey);
        }

        private void ClearOwedLocked()
        {
            _handedOver = null;
            _heardLow = _heardHigh = null;
            _lowAsked = _highAsked = _notWritten = _endStopSeq = 0;
            _lowLimitAt = _highLimitAt = null;
            _refusedPair = null;
        }

        /// <summary>
        /// The owed state belongs to one rig, Radio and connection; a request
        /// on another starts it afresh. The pair before the first request is
        /// what the operator is taken to know already.
        /// </summary>
        private void AdoptLocked(FlexBase rig, Radio? radio, object? connection)
        {
            if (!ReferenceEquals(rig, _stateRig) || !ReferenceEquals(radio, _stateRadio)
                || !ReferenceEquals(connection, _stateConnection))
            {
                ClearOwedLocked();
                _stateRig = rig;
                _stateRadio = radio;
                _stateConnection = connection;
            }
            if (_heardLow == null && radio != null)
            {
                _heardLow = rig.TXFilterLow;
                _heardHigh = rig.TXFilterHigh;
            }
        }

        /// <summary>
        /// A field moved to <paramref name="value"/>: by an arrow, a held key or
        /// typed entry, all alike. Writes that edge through the applied route
        /// and, once it has run, asks for the result to be said.
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
                AdoptLocked(rig, radio, connection);
                epoch = _epoch;
                request = lowField ? ++_lowNewest : ++_highNewest;
            }
            rig.SetTXFilter(lowField ? value : (int?)null, lowField ? (int?)null : value, applied =>
            {
                bool StillCurrent() => Answers(rig, radio, connection, epoch, lowField, request);
                if (!StillCurrent()) return;
                lock (_gate)
                {
                    long s = ++_seq;
                    if (applied.NotWritten)
                    {
                        _notWritten = s;
                        _refusedPair = rig.theRadio == null ? null : (rig.TXFilterLow, rig.TXFilterHigh);
                    }
                    else
                    {
                        _notWritten = 0;   // a later success makes an unsaid refusal untrue
                        if (lowField)
                        {
                            _lowAsked = s;
                            _lowLimitAt = applied.Low != applied.RequestedLow ? applied.Low : null;
                        }
                        else
                        {
                            _highAsked = s;
                            _highLimitAt = applied.High != applied.RequestedHigh ? applied.High : null;
                        }
                    }
                }
                _speak(_surfaceKey, Compose);
                _refresh?.Invoke(StillCurrent);
            });
        }

        /// <summary>
        /// A press against a field's end: the boundary word with that edge's
        /// value as the radio holds it when the sentence is spoken, covering
        /// whatever else is owed. <paramref name="shownValue"/> is used only
        /// when there is no radio to read. DRAFT.
        /// </summary>
        public void SayEndStop(bool lowField, bool atMinimum, int shownValue)
        {
            var rig = _rig();
            if (rig == null) return;
            Radio? radio = rig.theRadio;
            Watch(radio);
            lock (_gate)
            {
                AdoptLocked(rig, radio, radio?.CurrentCommandConnection);
                _endStopSeq = ++_seq;
                _endStopLow = lowField;
                _endStopAtMinimum = atMinimum;
                _endStopShown = shownValue;
            }
            _speak(_surfaceKey, Compose);
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

        /// <summary>
        /// The sentence, built at hand-over from the radio's pair NOW, stating
        /// every owed edge and an owed refusal, or null when nothing is owed or
        /// the surface's context has gone. DRAFT.
        /// </summary>
        private SpeechComposition? Compose()
        {
            lock (_gate)
            {
                var rig = _rig();
                if (rig == null || !ReferenceEquals(rig, _stateRig) || !_open()) return null;
                Radio? radio = rig.theRadio;
                if (!ReferenceEquals(radio, _stateRadio)
                    || !ReferenceEquals(radio?.CurrentCommandConnection, _stateConnection)) return null;

                var clauses = new List<string>();
                int? statedLow = null, statedHigh = null;
                long paidLow = 0, paidHigh = 0, paidEnd = 0;

                if (radio == null)
                {
                    // Nothing to read: only an end stop's shown value and a refusal can be said.
                    if (_endStopSeq != 0)
                    {
                        clauses.Add(EndClause(_endStopLow ? _lowLabel : _highLabel, _endStopShown, _endStopAtMinimum));
                        paidEnd = _endStopSeq;
                    }
                }
                else
                {
                    int low = rig.TXFilterLow, high = rig.TXFilterHigh;
                    bool lowEnd = _endStopSeq != 0 && _endStopLow, highEnd = _endStopSeq != 0 && !_endStopLow;
                    // The boundary word is decided NOW (round thirteen): an end
                    // stop says "maximum" or "minimum" only if the edge is still
                    // at that limit, FlexLib's own (low 0 to high minus 50, high
                    // low plus 50 to 10000). Otherwise it says the value plainly.
                    bool lowAtBoundary = lowEnd && (_endStopAtMinimum ? low <= 0 : low >= high - 50);
                    bool highAtBoundary = highEnd && (_endStopAtMinimum ? high <= low + 50 : high >= 10000);
                    // A refusal is about the pair it was refused against: once
                    // another door has moved the filter, "not taking changes
                    // right now" is no longer true, so it is dropped.
                    if (_notWritten != 0 && _refusedPair.HasValue && _refusedPair.Value != (low, high)) _notWritten = 0;
                    if (lowEnd || _lowAsked != 0 || low != _heardLow)
                    {
                        clauses.Add(lowAtBoundary ? EndClause(_lowLabel, low, _endStopAtMinimum)
                                                  : Clause(_lowLabel, low, _lowAsked != 0 && _lowLimitAt == low));
                        statedLow = low;
                        paidLow = _lowAsked;
                    }
                    if (highEnd || _highAsked != 0 || high != _heardHigh)
                    {
                        clauses.Add(highAtBoundary ? EndClause(_highLabel, high, _endStopAtMinimum)
                                                   : Clause(_highLabel, high, _highAsked != 0 && _highLimitAt == high));
                        statedHigh = high;
                        paidHigh = _highAsked;
                    }
                    if (lowEnd || highEnd) paidEnd = _endStopSeq;
                }
                long paidRefusal = _notWritten;
                if (paidRefusal != 0) clauses.Add(Lexicon.Get("audio.tx_filter.not_written"));

                // J35 (b): this sentence is being handed over, so the one
                // before it now counts as heard. Paid AFTER this sentence was
                // composed, so anything of it still owed has just been said
                // again above.
                if (_handedOver != null) PayLocked(_handedOver);
                _handedOver = null;
                if (clauses.Count == 0) return null;

                string text = string.Join(Lexicon.Get("audio.tx_filter.clause_joiner"), clauses);
                var spoken = new Spoken
                {
                    Epoch = _epoch,
                    StatedLow = statedLow,
                    StatedHigh = statedHigh,
                    PaidLow = paidLow,
                    PaidHigh = paidHigh,
                    PaidEnd = paidEnd,
                    PaidRefusal = paidRefusal,
                };
                _handedOver = spoken;
                return new SpeechComposition(text, outcome =>
                {
                    // Completed only (#916: this can arrive late or out of
                    // order); anything else waits for the next hand-over.
                    if (outcome.Kind != SpeechOutcomeKind.Completed) return;
                    lock (_gate)
                    {
                        PayLocked(spoken);
                        if (ReferenceEquals(_handedOver, spoken)) _handedOver = null;
                    }
                });
            }
        }

        /// <summary>
        /// Count one sentence as heard: credit only what its text stated, and
        /// pay only the obligations it carried, not newer ones raised since.
        /// Once per sentence, and not across an invalidation.
        /// </summary>
        private void PayLocked(Spoken spoken)
        {
            if (spoken.Paid || spoken.Epoch != _epoch) return;
            spoken.Paid = true;
            if (spoken.StatedLow.HasValue) _heardLow = spoken.StatedLow;
            if (spoken.StatedHigh.HasValue) _heardHigh = spoken.StatedHigh;
            if (spoken.PaidLow != 0 && _lowAsked == spoken.PaidLow) _lowAsked = 0;
            if (spoken.PaidHigh != 0 && _highAsked == spoken.PaidHigh) _highAsked = 0;
            if (spoken.PaidEnd != 0 && _endStopSeq == spoken.PaidEnd) _endStopSeq = 0;
            if (spoken.PaidRefusal != 0 && _notWritten == spoken.PaidRefusal) _notWritten = 0;
        }

        private static string Clause(string label, int value, bool atLimit)
            => Lexicon.Get("audio.field.spoken_value", ("label", label), ("value", value), ("unit", ""))
               + (atLimit ? Lexicon.Get("settings.filter.suffix_at_limit") : "");

        private static string EndClause(string label, int value, bool atMinimum)
            => Lexicon.Get(atMinimum ? "audio.field.spoken_value_minimum" : "audio.field.spoken_value_maximum",
                ("label", label), ("value", value), ("unit", ""));
    }
}
