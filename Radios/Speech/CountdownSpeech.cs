#nullable enable
using System;

namespace Radios.Speech
{
    /// <summary>
    /// The spoken half of the countdown before a check keys (#835): how its
    /// words reach the reader, whether each has started and finished, and
    /// taking them back when the count is called off. One policy, used by the
    /// live countdown through <see cref="ScreenReaderOutput.CountdownSpeech"/>
    /// and by tests on an arbiter and pump of their own, so a test exercises
    /// the same handoff the operator hears.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Codex round 1, 2026-10-28.</b> The numbers were queued on the pips'
    /// clock with nothing listening for delivery; they superseded one another;
    /// and a cancelled count's words stayed queued. So each piece is ADDITIVE
    /// on <see cref="SpeechSubject.CheckCountdown"/>, the count follows each
    /// one, and <see cref="Withdraw"/> supersedes the subject, which takes
    /// every unsent piece back out of the delivery pump.
    /// </para>
    /// <para>
    /// <b>Codex round 2.</b> The count now follows each piece individually,
    /// not the subject as a whole:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Started</b>, so a pip waits for its own number. On a reader
    /// that reports completion (NVDA's channel), a piece has started when the
    /// reader reports its FIRST WORD MARK; being handed to the reader is not
    /// enough, since the reader can hold it behind speech of its own (Codex
    /// round 3). On the estimate path it has started when its estimated start
    /// has passed.</item>
    /// <item><b>Finished.</b> Tracked: when the reader says so (or it was cut
    /// part-way, which for a one-word number is heard). Untracked: an expired
    /// estimate is NOT proof, so where these words are the only warning
    /// (sounds off), a piece counts as finished only at its estimated finish
    /// plus <see cref="UntrackedMarginMs"/>. With sounds on, the pips are the
    /// warning and the estimate alone paces the words.</item>
    /// <item><b>Failed</b>, when the reader was never reached, so nothing
    /// entered the ledger, or the piece was taken back. A failed piece is
    /// never "said": the count treats it as owed and, where the words are the
    /// warning, stops without keying.</item>
    /// </list>
    /// <para>
    /// What no reader tells us, the estimate path cannot either: speech JAWS or
    /// SAPI says of its own (a focus announcement) sits outside our ledger. The
    /// margin is the honest allowance for it; the ASK of 2026-10-28 puts the
    /// choice of a stronger rule to Noel.
    /// </para>
    /// </remarks>
    public sealed class CountdownSpeech
    {
        /// <summary>The least margin added to an untracked piece's estimated finish when the words are the only warning.</summary>
        public const int UntrackedMarginFloorMs = 1000;

        /// <summary>And the margin's share of the piece's own estimated length, in percent, when that is larger.</summary>
        public const int UntrackedMarginPercent = 50;

        private readonly SpeechArbiter _arbiter;
        private readonly Func<long, bool>? _reachedFirstWord;
        private readonly Func<System.Threading.Tasks.Task>? _nextProgress;

        /// <param name="arbiter">The arbiter the words go through.</param>
        /// <param name="reachedFirstWord">Whether the reader has reported the first
        /// word of a ticket. Null means no such signal: a tracked piece then
        /// counts as started only once it has finished.</param>
        /// <param name="nextProgress">The reader's next report of progress
        /// (<see cref="PacedSpeechDelivery.NextProgress"/>), which wakes the
        /// count early (#855). Null means none: the count polls.</param>
        internal CountdownSpeech(SpeechArbiter arbiter, Func<long, bool>? reachedFirstWord = null,
            Func<System.Threading.Tasks.Task>? nextProgress = null)
        {
            _arbiter = arbiter;
            _reachedFirstWord = reachedFirstWord;
            _nextProgress = nextProgress;
        }

        /// <summary>
        /// The margin for an untracked piece whose estimated length is
        /// <paramref name="estimatedMs"/>: the larger of
        /// <see cref="UntrackedMarginFloorMs"/> and
        /// <see cref="UntrackedMarginPercent"/> of the estimate.
        /// </summary>
        public static int UntrackedMarginMs(int estimatedMs) =>
            Math.Max(UntrackedMarginFloorMs, Math.Max(estimatedMs, 0) * UntrackedMarginPercent / 100);

        /// <summary>
        /// Queue one piece of the count (the lead-in, or a number) and return
        /// its handle. Throws if the speech layer throws; the count treats
        /// that as a piece never said.
        /// </summary>
        /// <param name="text">The words.</param>
        /// <param name="onlyWarning">True when these words are the only warning
        /// (sounds off): an untracked piece is then held for its margin.</param>
        public Radios.ICountdownWords Say(string text, bool onlyWarning)
        {
            if (string.IsNullOrEmpty(text)) return Radios.CountdownWords.Undelivered;
            var piece = _arbiter.EmitQueued(text, VerbosityLevel.Critical, "CountdownSpeech.Say",
                SpeechSubject.CheckCountdown, additive: true);
            return piece == null ? Radios.CountdownWords.Undelivered : new Words(this, piece, onlyWarning);
        }

        /// <summary>
        /// Queue one sentence that must be HEARD IN FULL before something
        /// starts, on its own subject, through the count's own road (Critical,
        /// queued, additive), and return a handle that counts it said only when
        /// it was (Sprint 49 Track Q3 repair 1: the audition's "Recording",
        /// said before the microphone opens; Codex's check of 83932446, C2).
        /// Taken back by <see cref="ScreenReaderOutput.Supersede"/> on that subject.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not the count's rule.</b> A number of the count cut part-way is
        /// counted as heard, on purpose (a one-word number that has begun is
        /// heard), and <see cref="Say(string, bool)"/> keeps that rule
        /// unchanged. Track Q3 reused it for "Recording", so a "Recording" cut
        /// at its first word mark, by us or by anything else, opened the take.
        /// Here:
        /// </para>
        /// <list type="bullet">
        /// <item><b>Finished</b>, on a reader that reports completion, only
        /// when the reader reported it completed and it was never cut part-way
        /// at any attempt. On the estimate path, as for the count: at its
        /// estimated finish, plus the margin where it is the only warning.</item>
        /// <item><b>Failed</b> when it was taken back, or cut part-way at any
        /// attempt: a word that was cut was not heard in full, and saying it
        /// again whole later does not make the first hearing whole.</item>
        /// </list>
        /// <para>
        /// What the estimate path cannot see, a cut included, it cannot report;
        /// that is unchanged, and the trace says which path a word took.
        /// </para>
        /// </remarks>
        /// <param name="text">The words.</param>
        /// <param name="onlyWarning">True when these words are the only warning, as for <see cref="Say(string, bool)"/>.</param>
        /// <param name="subject">A <see cref="SpeechSubject"/> constant.</param>
        public Radios.ICountdownWords SayInFull(string text, bool onlyWarning, string subject)
        {
            if (string.IsNullOrEmpty(text)) return Radios.CountdownWords.Undelivered;
            var piece = _arbiter.EmitQueued(text, VerbosityLevel.Critical, "CountdownSpeech.SayInFull",
                subject, additive: true);
            return piece == null ? Radios.CountdownWords.Undelivered : new InFullWords(new Words(this, piece, onlyWarning), this, piece);
        }

        /// <summary>
        /// Take back every piece not yet handed to the reader: the count was
        /// called off, or its words could not be delivered.
        /// </summary>
        /// <param name="by">Plain prose for the trace, after "superseded by".</param>
        public void Withdraw(string by) =>
            _arbiter.Supersede(SpeechSubject.CheckCountdown, by, "CountdownSpeech.Withdraw");

        /// <summary>
        /// <see cref="SayInFull"/>'s handle: the count's own handle for when
        /// it started and for news, and the arbiter's completion record for
        /// whether it was heard in full.
        /// </summary>
        private sealed class InFullWords : Radios.ICountdownWords
        {
            private readonly Words _count;
            private readonly CountdownSpeech _owner;
            private readonly SpeechArbiter.FollowedPiece _piece;

            public InFullWords(Words count, CountdownSpeech owner, SpeechArbiter.FollowedPiece piece)
            {
                _count = count;
                _owner = owner;
                _piece = piece;
            }

            public bool Failed
            {
                get
                {
                    if (_owner._arbiter.Inspect(_piece).Superseded) return true;
                    return _owner._arbiter.Completion(_piece).EverCut;
                }
            }

            public bool Started => _count.Started;

            public System.Threading.Tasks.Task NextNews => _count.NextNews;

            public bool Finished
            {
                get
                {
                    var v = _owner._arbiter.Inspect(_piece);
                    if (v.Superseded) return false;
                    var (completed, everCut) = _owner._arbiter.Completion(_piece);
                    if (everCut) return false;
                    if (completed) return true;
                    // Never answered for by a reader: the estimate path, which
                    // holds an untracked word exactly as the count does.
                    if (v.Tracked) return false;
                    return v.State == SpeechArbiter.Owed.Pending && _count.Finished;
                }
            }
        }

        private sealed class Words : Radios.ICountdownWords
        {
            private readonly CountdownSpeech _owner;
            private readonly SpeechArbiter.FollowedPiece _piece;
            private readonly bool _onlyWarning;

            public Words(CountdownSpeech owner, SpeechArbiter.FollowedPiece piece, bool onlyWarning)
            {
                _owner = owner;
                _piece = piece;
                _onlyWarning = onlyWarning;
            }

            private SpeechArbiter.FollowedView View => _owner._arbiter.Inspect(_piece);

            public bool Failed => View.Superseded;

            public System.Threading.Tasks.Task NextNews =>
                _owner._nextProgress?.Invoke() ?? Radios.CountdownWords.NoNews;

            public bool Started
            {
                get
                {
                    var v = View;
                    if (v.Superseded) return false;
                    if (v.State == SpeechArbiter.Owed.NeverStarted) return false;
                    if (v.State != SpeechArbiter.Owed.Pending) return true;   // cut part-way: it started
                    if (v.Tracked)
                    {
                        if (!v.InLedger) return true;                          // the reader finished it
                        // Only the reader's first word mark says it has begun.
                        return _owner._reachedFirstWord != null && _owner._reachedFirstWord(v.Ticket);
                    }
                    return v.NowUtc >= v.EstStartUtc;
                }
            }

            public bool Finished
            {
                get
                {
                    var v = View;
                    if (v.Superseded) return false;
                    if (v.State == SpeechArbiter.Owed.NeverStarted) return false;
                    if (v.State != SpeechArbiter.Owed.Pending) return true;   // a one-word piece cut part-way was heard
                    if (v.Tracked) return !v.InLedger;
                    // The estimate path: an expired estimate is not proof. Where
                    // these words are the only warning, hold on for the margin.
                    var finish = v.EstFinishUtc;
                    if (_onlyWarning)
                        finish = finish.AddMilliseconds(UntrackedMarginMs((int)(v.EstFinishUtc - v.EstStartUtc).TotalMilliseconds));
                    return v.NowUtc >= finish;
                }
            }
        }
    }
}
