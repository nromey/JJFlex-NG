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
    /// <b>Bounded.</b> If nothing it handed over is said or let go for
    /// <see cref="StallMs"/> (a reader that refuses, or lines a foreign cancel
    /// gave back and no later interrupt rescued), the series ends, takes back
    /// what it handed over and drops the rest, and the trace says so.
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

        /// <summary>How often a series with lines waiting looks at what it handed over.</summary>
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
        private readonly Queue<(string Text, VerbosityLevel Level)> _waiting = new Queue<(string, VerbosityLevel)>();
        private readonly List<SpeechArbiter.FollowedPiece> _handed = new List<SpeechArbiter.FollowedPiece>();
        // Every piece ever handed over, kept until the series ends: one the
        // shared ledger let go of at its cap reads as done, but may still be
        // waiting in the pump, and only its own handle can take it back.
        private readonly List<SpeechArbiter.FollowedPiece> _everHanded = new List<SpeechArbiter.FollowedPiece>();
        private Timer? _timer;
        private string? _endedBy;
        private long _lastProgressMs;

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
                _waiting.Enqueue((text, level));
                PumpLocked();
            }
        }

        /// <summary>
        /// True while any line is waiting, or handed over and not yet said or
        /// let go (a line held for its put-back after an interrupt is still
        /// owed). False once the series has ended.
        /// </summary>
        public bool Owes
        {
            get
            {
                lock (_lock)
                {
                    if (_endedBy != null) return false;
                    if (_waiting.Count > 0) return true;
                    foreach (var p in _handed) if (!Done(p)) return true;
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
            if (_handed.RemoveAll(Done) > 0) _lastProgressMs = now;
            if (_waiting.Count == 0)
            {
                StopTimerLocked();
                return;
            }
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

            while (_waiting.Count > 0 && _handed.Count < BatchSize)
            {
                var (text, level) = _waiting.Dequeue();
                SpeechArbiter.FollowedPiece? piece;
                try { piece = _arbiter.EmitQueued(text, level, _origin, _subject, additive: true); }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"SpokenSeries [{_subject}]: a line could not be handed over ({ex.Message}): '{Clip(text)}'",
                        TraceLevel.Warning);
                    piece = null;
                }
                // Null: the reader was not reached (speech suppressed, or no
                // backend), so nothing entered the ledger and nothing is owed.
                if (piece != null) { _handed.Add(piece); _everHanded.Add(piece); }
            }
            _lastProgressMs = now;
            if (_waiting.Count > 0) StartTimerLocked(); else StopTimerLocked();
        }

        /// <summary>
        /// Whether a handed piece no longer stands in the way of the next line:
        /// said, taken back, let go, or discarded. A piece HELD for its
        /// put-back after an interrupt stands in the way, whatever its old
        /// estimate says, because anything handed over now would be said
        /// before it.
        /// </summary>
        private bool Done(SpeechArbiter.FollowedPiece piece)
        {
            var v = _arbiter.Inspect(piece);
            if (v.Superseded || !v.InLedger) return true;
            if (_arbiter.IsHeld(piece)) return false;
            // Cut part-way by something that is not us (the operator's Ctrl, a
            // focus change): the arbiter will not put it back on its own, and
            // the reader is free.
            if (v.State == SpeechArbiter.Owed.PausedUnknownCause) return true;
            if (v.Tracked) return false;   // the reader will say when it has finished
            if (v.State != SpeechArbiter.Owed.Pending) return false;
            return v.NowUtc >= v.EstFinishUtc;
        }

        private void EndLocked(string by)
        {
            _endedBy = by;
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
