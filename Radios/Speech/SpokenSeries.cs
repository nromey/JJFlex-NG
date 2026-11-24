#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using JJTrace;

namespace Radios.Speech
{
    /// <summary>
    /// A run of lines on one subject that are ALL to be heard, in the order
    /// they were made, and that the caller can take back at any moment: the
    /// Audio Workshop's speed test results (Sprint 49 Track L2 repair 1, #866;
    /// Codex's check of 979bfa1f, B1 to B3). It owns every line from the
    /// moment it is added until the reader has said it or it is taken back,
    /// whatever the code that made the lines is doing meanwhile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No new delivery.</b> Each line is a queued, ADDITIVE utterance on the
    /// subject, through <see cref="SpeechArbiter.EmitQueued"/>, the countdown's
    /// own road (<see cref="CountdownSpeech"/>), so no line retires another and
    /// every interrupt, salvage and Urgent rule applies to it exactly as to any
    /// other queued speech. What this class adds is only WHEN each line is
    /// handed over, and the handle to take it back.
    /// </para>
    /// <para>
    /// <b>Drain, then hand over at most <see cref="BatchSize"/> (B2, B3).</b>
    /// A line is handed to the reader only when nothing this series handed
    /// earlier is still owed. Two things follow. The series never holds more
    /// than <see cref="BatchSize"/> entries of the arbiter's shared ledger,
    /// which forgets its oldest ordinary entry at 16 without taking it out of
    /// the delivery pump, so an 18-line result list can no longer lose lines to
    /// that cap, and the cap stays as it is. And after any interrupt, ours or
    /// anyone's, the series' own lines are HELD for their put-back; while one
    /// is held the series hands nothing new, so nothing it makes later can be
    /// said ahead of the lines the interrupt rescued, and its last line
    /// ("finished") follows every line that survived.
    /// </para>
    /// <para>
    /// <b>Warnings keep their priority.</b> An Urgent warning discards
    /// ordinary speech, the series' lines included; a discarded line is no
    /// longer owed, so the series carries on with its next lines, behind the
    /// warning. The operator's own Silence ENDS the series: nothing more of it
    /// is said, as nothing more of any queue is.
    /// </para>
    /// <para>
    /// <b>A cut made outside the app pauses the series, and the series keeps
    /// its own place (Track SL, #1228).</b> When something that is not us
    /// (NVDA's own Ctrl, a focus change) cuts the reader, the pump gives back
    /// everything queued behind the line that was sounding unspoken. The
    /// series notices either shape of that cut: its own line cut part-way, or
    /// its lines given back at word 0 while some other utterance was sounding.
    /// It then TAKES BACK every handed line that is owed and not with the
    /// reader (never started in its last attempt, or cut by us; repair 1 widened
    /// this from "never said a word"), withdrawing it from the arbiter so no
    /// put-back of the arbiter's can say it too, and puts it back at its own
    /// place among its waiting lines, to be said in full.
    /// The line cut part-way is not taken back and not said again; that is the
    /// arbiter's rule for a cut nobody can name, unchanged. The series then
    /// hands nothing until an ordinary interrupt of ours, anywhere in the app,
    /// has reached the reader since the cut (or an urgent warning or alarm has
    /// discarded ordinary speech, behind which the series carries on), and
    /// that interrupt's settle window has passed, so its own follow-ups are
    /// heard first (#507). Ruled by Noel: 2026-11-20 (S2, the list keeps its
    /// place however long the pause, so the arbiter's 15-second salvage
    /// lifetime no longer decides it for a list), 2026-11-23 (S4, no time limit
    /// after Ctrl) and 2026-11-23 20:38 (the next key anywhere resumes it).
    /// Before Track SL the lines given back waited in the arbiter's ledger,
    /// and a key more than 15 seconds later found them gone and the series
    /// carried on after them (bench 4a).
    /// </para>
    /// <para>
    /// <b>A cut of the speech the list waits behind counts too (repair 1,
    /// bench 2026-11-23 runs 3b and 3d).</b> After one of our keys the list's
    /// lines are held behind the key's own speech for its settle window; if
    /// NVDA's Ctrl cuts that speech meanwhile, the arbiter does not release
    /// the held series lines into the silence (<see cref="SpeechArbiter.EmitSeriesLine"/>
    /// marks them), and the series pauses exactly as for a cut of its own
    /// line. While paused, every later cut from outside moves the pause on to
    /// itself, so the key that resumes the list is one whose interrupt reached
    /// the reader after the newest cut, as the arbiter serialises them.
    /// </para>
    /// <para>
    /// <b>Bounded, except while paused by a cut from outside.</b> If nothing
    /// it handed over is said or let go for <see cref="StallMs"/> (a reader
    /// that went silent with nobody pressing Ctrl), the series ends, takes
    /// back what it handed over and drops the rest, and the trace says so. The
    /// bound watches every handed line until it is retired, the final batch
    /// included (repair 2, C2), and is measured from the last line handed over
    /// or retired. While the series is paused by a cut from outside the bound
    /// does not run (ruled S4): it waits for the operator's next key however
    /// long, and ends only when it is withdrawn (the Audio Workshop closing, a
    /// new test starting) or the operator silences speech. Resuming starts the
    /// stall clock afresh. A paused series polls on its timer, allocating
    /// nothing, and holds nothing that grows with time.
    /// </para>
    /// </remarks>
    public sealed class SpokenSeries
    {
        /// <summary>
        /// Most lines handed to the reader at once. Small, so the series leaves
        /// most of the arbiter's 16-entry ledger to everything else; more than
        /// one, so on a reader with no completion channel (the estimate path)
        /// a generous estimate costs one pause per few lines rather than one
        /// per line. On a reader that reports completion the pause between
        /// batches is one poll.
        /// </summary>
        internal const int BatchSize = 4;

        /// <summary>How often a series with lines waiting, or handed over and still owed, looks at what it handed over.</summary>
        internal const int PollMs = 40;

        /// <summary>How long a series waits with nothing it handed over said or let go, before it ends.</summary>
        internal const int StallMs = 60000;

        private readonly SpeechArbiter _arbiter;
        private readonly string _subject;
        private readonly string _origin;
        private readonly Func<VerbosityLevel> _verbosity;
        private readonly Action<string, VerbosityLevel>? _gated;
        private readonly int _pollMs;
        private readonly Func<long> _nowMs;
        private readonly long _quietGeneration;

        private readonly object _lock = new object();

        /// <summary>One line, with its place in the order it was added, so a line taken back goes back where it was.</summary>
        private readonly record struct Line(long Seq, string Text, VerbosityLevel Level);

        // Lines not handed to the reader, in the order they were added. A line
        // taken back after a cut from outside goes back to its own place,
        // which is ahead of every line never handed (Track SL).
        private readonly List<Line> _waiting = new List<Line>();
        // Lines handed over and not yet retired, each with its text and level
        // so it can be taken back and handed over again (Track SL).
        private readonly List<(SpeechArbiter.FollowedPiece Piece, Line Line)> _handed = new List<(SpeechArbiter.FollowedPiece, Line)>();
        // Every piece ever handed over, kept until the series ends: one the
        // shared ledger let go of at its cap reads as done, but may still be
        // waiting in the pump, and only its own handle can take it back.
        private readonly List<SpeechArbiter.FollowedPiece> _everHanded = new List<SpeechArbiter.FollowedPiece>();
        private Timer? _timer;
        private string? _endedBy;
        private long _lastProgressMs;
        private long _nextSeq;

        // Paused by a cut from outside (Track SL, #1228): nothing is handed
        // until an ordinary interrupt of ours has reached the reader since the
        // cut (the arbiter's count passes _pausedAtInterrupt), or an urgent
        // warning's discard has been made since (its count passes
        // _pausedAtDiscard), and the arbiter says that interrupt has settled.
        // The series' own record, tied to no ledger entry, so no lifetime of
        // the arbiter's can end it.
        private bool _paused;
        private long _pausedAtInterrupt;
        private long _pausedAtDiscard;
        // The arbiter's count of cuts from outside, as of this series' last
        // look (repair 1).
        private long _foreignCutsSeen;

        /// <param name="arbiter">The arbiter the lines go through.</param>
        /// <param name="subject">Every line's subject.</param>
        /// <param name="origin">The arbiter's origin for every line.</param>
        /// <param name="verbosity">The operator's verbosity; a line above it is not said. Null: everything is said.</param>
        /// <param name="gated">Told of a line the verbosity kept back, so "filtered" and "never made" stay apart.</param>
        /// <param name="pollMs">How often to look while lines wait; 0 means never by itself (tests call <see cref="Pump"/>).</param>
        /// <param name="nowMs">Milliseconds, for the stall bound only. Null: <see cref="Environment.TickCount64"/>.</param>
        internal SpokenSeries(SpeechArbiter arbiter, string subject, string origin,
            Func<VerbosityLevel>? verbosity = null, Action<string, VerbosityLevel>? gated = null,
            int pollMs = PollMs, Func<long>? nowMs = null)
        {
            _arbiter = arbiter;
            _subject = subject;
            _origin = origin;
            _verbosity = verbosity ?? (() => VerbosityLevel.Chatty);
            _gated = gated;
            _pollMs = pollMs;
            _nowMs = nowMs ?? (() => Environment.TickCount64);
            _quietGeneration = arbiter.SafetyQuietGeneration;
            _foreignCutsSeen = arbiter.Interrupts().ForeignCuts;
        }

        /// <summary>
        /// Add a line at the end: said after every line added before it,
        /// unless the series is taken back first. Any thread.
        /// </summary>
        public void Add(string text, VerbosityLevel level)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_lock)
            {
                if (_endedBy != null) return;
                if ((int)level > (int)_verbosity())
                {
                    _gated?.Invoke(text, level);
                    return;
                }
                // Nothing owed until now: the stall clock starts with this line.
                if (_waiting.Count == 0 && _handed.Count == 0) _lastProgressMs = _nowMs();
                _waiting.Add(new Line(_nextSeq++, text, level));
                PumpLocked();
            }
        }

        /// <summary>
        /// True while any line is waiting, or handed over and not yet said or
        /// let go (a line held for its put-back after an interrupt is still
        /// owed, and so is every line of a series paused by a cut from
        /// outside). False once the series has ended.
        /// </summary>
        public bool Owes
        {
            get
            {
                lock (_lock)
                {
                    if (_endedBy != null) return false;
                    if (_waiting.Count > 0) return true;
                    for (int i = 0; i < _handed.Count; i++) if (!Done(_handed[i].Piece)) return true;
                    return false;
                }
            }
        }

        /// <summary>
        /// Take the series back: drop every line not yet handed over, and take
        /// back every handed one the reader has not been given yet (by its own
        /// ticket, so a line the shared ledger has let go of is still found).
        /// What the reader is saying is not cut. Nothing is added after this.
        /// Any thread; a second call does nothing.
        /// </summary>
        /// <param name="by">Plain prose for the trace, after "superseded by".</param>
        public void Withdraw(string by)
        {
            lock (_lock)
            {
                if (_endedBy != null) return;
                EndLocked(by);
            }
        }

        /// <summary>Look at what was handed over and hand over more if it may. The timer's work; tests call it.</summary>
        internal void Pump()
        {
            lock (_lock) PumpLocked();
        }

        private void PumpLocked()
        {
            if (_endedBy != null) return;
            if (_arbiter.SafetyQuietGeneration != _quietGeneration)
            {
                EndLocked("the operator silenced speech");
                return;
            }

            long now = _nowMs();
            var interrupts = _arbiter.Interrupts();
            // A cut from outside the series has not yet looked at (repair 1).
            // Read every pump, the ones that hand over included, so a cut made
            // before a batch was handed is never mistaken for one made after.
            bool newForeignCut = interrupts.ForeignCuts > _foreignCutsSeen;
            _foreignCutsSeen = interrupts.ForeignCuts;
            if (_handed.Count > 0 || (_paused && newForeignCut)) SweepHandedLocked(now, newForeignCut, interrupts);

            if (_paused)
            {
                bool keyed = interrupts.Reached > _pausedAtInterrupt || interrupts.Discards > _pausedAtDiscard;
                if (!keyed || !interrupts.Settled)
                {
                    // No stall bound while paused (ruled S4): the series waits
                    // for our next interrupt however long. It looks again on its
                    // timer while it has anything to give, allocating nothing;
                    // with nothing left it rests until a line is added, and the
                    // pause, which is its own record, is still there then.
                    if (_waiting.Count > 0 || _handed.Count > 0) StartTimerLocked(); else StopTimerLocked();
                    return;
                }
                _paused = false;
                _lastProgressMs = now;   // the stall clock starts afresh
                Tracing.TraceLine(
                    $"SpokenSeries [{_subject}]: resumed after a cut from outside, "
                    + (interrupts.Reached > _pausedAtInterrupt
                        ? "our next interrupt reached the reader and its settle window passed"
                        : "an urgent warning discarded ordinary speech")
                    + $": {_waiting.Count} line(s) waiting, {_handed.Count} still handed over",
                    TraceLevel.Info);
            }

            // Anything handed over and still owed is watched, the final batch
            // included: before repair 2 the watch stopped as soon as nothing
            // was left waiting, so a last batch the reader never answered was
            // owed for ever (Codex's re-check of 03280742, C2).
            if (_handed.Count > 0)
            {
                if (now - _lastProgressMs >= StallMs)
                {
                    EndLocked($"nothing it handed over was said or let go for {StallMs} ms");
                    return;
                }
                StartTimerLocked();
                return;
            }
            if (_waiting.Count == 0)
            {
                StopTimerLocked();
                return;
            }

            while (_waiting.Count > 0 && _handed.Count < BatchSize)
            {
                var line = _waiting[0];
                _waiting.RemoveAt(0);
                SpeechArbiter.FollowedPiece? piece;
                try { piece = _arbiter.EmitSeriesLine(line.Text, line.Level, _origin, _subject); }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"SpokenSeries [{_subject}]: a line could not be handed over ({ex.Message}): '{Clip(line.Text)}'",
                        TraceLevel.Warning);
                    piece = null;
                }
                // Null: the reader was not reached (speech suppressed, or no
                // backend), so nothing entered the ledger and nothing is owed.
                if (piece != null) { _handed.Add((piece, line)); _everHanded.Add(piece); }
            }
            _lastProgressMs = now;
            // Watched until what was just handed over is retired (C2). With
            // nothing handed (no reader reached), nothing is left waiting either.
            if (_handed.Count > 0 || _waiting.Count > 0) StartTimerLocked(); else StopTimerLocked();
        }

        /// <summary>
        /// Retire what the reader has finished with, and act on a cut from
        /// outside (Track SL, #1228). Allocates nothing unless something
        /// changed.
        /// </summary>
        /// <remarks>
        /// A cut from outside is recognised by the arbiter's own report on any
        /// handed line, whichever shape it took: a line of ours cut part-way,
        /// or lines of ours given back at word 0 while another utterance was
        /// sounding; and (repair 1) a cut from outside of anything while a line
        /// it handed over is still owed, which is how a cut of the speech the
        /// list waits behind shows. From then until the pause ends, every
        /// handed line that is owed and not with the reader is withdrawn from
        /// the arbiter and goes back to its own place in
        /// <see cref="_waiting"/>, to be said in full; a line cut part-way from
        /// outside is let go, not repeated. A line the pump has not yet reported on
        /// (its reports arrive one at a time) is left alone until its own
        /// report comes, so nothing is withdrawn that the reader may already
        /// have; a later sweep takes it back.
        /// </remarks>
        private void SweepHandedLocked(long now, bool newForeignCut, SpeechArbiter.InterruptView interrupts)
        {
            int cutsSeen = 0;
            long cutAt = -1, discardAt = -1;
            for (int i = 0; i < _handed.Count; i++)
            {
                var cut = _arbiter.LastCut(_handed[i].Piece);
                if (!cut.FromOutside) continue;
                cutsSeen++;
                if (cut.AtInterrupt > cutAt) cutAt = cut.AtInterrupt;
                if (cut.AtDiscard > discardAt) discardAt = cut.AtDiscard;
            }
            // Repair 1: a cut from outside that landed on speech the list was
            // WAITING BEHIND counts as one on the list's own line. The list
            // waits behind something whenever a line it handed over is still
            // owed: held by the arbiter behind our interrupt's own speech
            // (bench run 3b: NVDA's Ctrl cut only the key's help, inside its
            // settle window), or queued behind another utterance. And while
            // the list is paused, every later cut from outside moves the pause
            // on to that cut, so only an interrupt of ours that reached the
            // reader AFTER the newest cut resumes it (run 3d: NVDA's Ctrl cut
            // the help of the very key that had counted as the next key).
            bool rebaselined = false;
            if (newForeignCut && (_paused || AnyOwedLocked()))
            {
                cutsSeen++;
                if (interrupts.LastForeignCutAtInterrupt > cutAt) cutAt = interrupts.LastForeignCutAtInterrupt;
                if (interrupts.LastForeignCutAtDiscard > discardAt) discardAt = interrupts.LastForeignCutAtDiscard;
                rebaselined = _paused;
            }
            bool newlyPaused = false;
            if (cutsSeen > 0)
            {
                // The pause is measured from the cut, as the arbiter recorded
                // it, not from when the series noticed it: an interrupt of ours
                // that came between the two still ends it. The latest cut
                // counts, because the reader was silenced again by it.
                if (!_paused)
                {
                    _paused = true;
                    newlyPaused = true;
                    _pausedAtInterrupt = cutAt;
                    _pausedAtDiscard = discardAt;
                }
                else
                {
                    if (cutAt > _pausedAtInterrupt) _pausedAtInterrupt = cutAt;
                    if (discardAt > _pausedAtDiscard) _pausedAtDiscard = discardAt;
                }
            }

            int takenBack = 0, cutPartway = 0;
            bool progress = false;
            for (int i = _handed.Count - 1; i >= 0; i--)
            {
                var (piece, line) = _handed[i];
                if (_paused)
                {
                    var cut = _arbiter.LastCut(piece);
                    if (cut.Resumable)
                    {
                        // Owed and not with the reader: never started in its
                        // last attempt (given back at word 0), or cut by us at
                        // any word. Ours to say again, in full and in its
                        // place, as the arbiter's own put-back would have said
                        // it; but the arbiter's 15-second lifetime no longer
                        // decides it (repair 1, finding 1: a line our key cut
                        // part-way and NVDA's Ctrl then cut again was lost at
                        // 15 s). Withdrawn first, so no put-back of the
                        // arbiter's can say it as well: marked superseded, and
                        // a held copy is dropped at its release.
                        _arbiter.Withdraw(piece, "its own spoken series, which keeps its place after a cut from outside", _origin);
                        _handed.RemoveAt(i);
                        InsertWaitingLocked(line);
                        takenBack++;
                        continue;
                    }
                    if (cut.FromOutside)
                    {
                        // Cut part-way by something that is not us: heard in
                        // part, and not said again by anyone.
                        _handed.RemoveAt(i);
                        cutPartway++;
                        continue;
                    }
                }
                if (Done(piece))
                {
                    _handed.RemoveAt(i);
                    progress = true;
                }
            }
            if (progress) _lastProgressMs = now;

            if (newlyPaused)
            {
                Tracing.TraceLine(
                    $"SpokenSeries [{_subject}]: paused by a cut from outside: {takenBack} line(s) taken back, "
                    + $"{cutPartway} cut part-way not to be repeated, waiting for the next key",
                    TraceLevel.Info);
            }
            else if (takenBack > 0 || cutPartway > 0 || rebaselined)
            {
                Tracing.TraceLine(
                    $"SpokenSeries [{_subject}]: still paused by a cut from outside: {takenBack} more line(s) taken back, "
                    + $"{cutPartway} more cut part-way not to be repeated"
                    + (rebaselined ? ", and a further cut from outside means only a key after it resumes the list" : string.Empty),
                    TraceLevel.Info);
            }
        }

        /// <summary>Whether any line handed over is still owed (held, queued, or cut and unresolved). Allocates nothing.</summary>
        private bool AnyOwedLocked()
        {
            for (int i = 0; i < _handed.Count; i++) if (!Done(_handed[i].Piece)) return true;
            return false;
        }

        /// <summary>A line taken back goes back to its own place: behind any taken back before it, ahead of every line added after it.</summary>
        private void InsertWaitingLocked(Line line)
        {
            int at = 0;
            while (at < _waiting.Count && _waiting[at].Seq < line.Seq) at++;
            _waiting.Insert(at, line);
        }

        /// <summary>
        /// Whether a handed piece no longer stands in the way of the next line:
        /// said, taken back, let go, or discarded. A piece HELD for its
        /// put-back after an interrupt stands in the way, whatever its old
        /// estimate says, because anything handed over now would be said
        /// before it. A cut from outside is not judged here but by the
        /// series' own pause (<see cref="SweepHandedLocked"/>); repair 2's wait
        /// on the cut line's ledger entry, which the arbiter's 15-second
        /// lifetime could prune, is gone (Track SL, #1228).
        /// </summary>
        private bool Done(SpeechArbiter.FollowedPiece piece)
        {
            var v = _arbiter.Inspect(piece);
            if (v.Superseded || !v.InLedger) return true;
            if (_arbiter.IsHeld(piece)) return false;
            if (v.Tracked) return false;   // the reader will say when it has finished
            if (v.State != SpeechArbiter.Owed.Pending) return false;
            return v.NowUtc >= v.EstFinishUtc;
        }

        private void EndLocked(string by)
        {
            _endedBy = by;
            _paused = false;
            StopTimerLocked();
            int dropped = _waiting.Count;
            _waiting.Clear();
            int takenBack = 0;
            // Every piece, not only those still believed owed: the pump answers
            // no for one already said, and nothing the reader has is cut.
            foreach (var p in _everHanded)
                if (_arbiter.Withdraw(p, by, _origin)) takenBack++;
            _handed.Clear();
            _everHanded.Clear();
            if (dropped > 0 || takenBack > 0)
            {
                Tracing.TraceLine(
                    $"SpokenSeries [{_subject}]: ended, {by}: {dropped} line(s) not yet handed over dropped, "
                    + $"{takenBack} handed over and still owed taken back",
                    TraceLevel.Info);
            }
        }

        private void StartTimerLocked()
        {
            if (_pollMs <= 0 || _timer != null) return;
            _timer = new Timer(_ => Pump(), null, _pollMs, _pollMs);
        }

        private void StopTimerLocked()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private static string Clip(string s) => s.Length > 60 ? s.Substring(0, 60) + "…" : s;
    }
}
