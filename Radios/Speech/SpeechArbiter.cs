#nullable enable
using System;
using System.Collections.Generic;

namespace Radios.Speech
{
    /// <summary>
    /// The single point where speech text is actually handed onward. Returns
    /// true when the text reached the backend (not suppressed, backend
    /// available) — the arbiter's believed-pending ledger keys on that,
    /// because an utterance that never reached the reader occupied nothing
    /// and destroyed nothing.
    /// <paramref name="salvaged"/> marks a re-emission of a queued utterance
    /// an interrupt would otherwise have destroyed; the sink records it as
    /// such and keeps it out of the repeat-history it is already in.
    /// </summary>
    internal delegate bool SpeechSink(
        string message, bool interrupt, SpeechIntent? intent,
        VerbosityLevel? level, string? origin, bool salvaged);

    /// <summary>
    /// The speech timing-and-order brain: intent arbitration happens once,
    /// centrally, where the pending state is (see <see cref="SpeechIntent"/>).
    /// Owns the Latest coalescer (lead-then-settle) and the believed-pending
    /// ledger that stops an interrupt from silently destroying queued speech.
    ///
    /// **Why the ledger exists.** A screen reader's cancel primitive flushes
    /// its ENTIRE queue, not just the utterance in progress. So before Sprint
    /// 35, any <c>interrupt=true</c> emission destroyed every queued utterance
    /// nobody had heard yet — and the transcript still said <c>Spoke</c> for
    /// every one of them. Caught live on 2026-08-25: one keypress produced six
    /// utterances; three were queued, and an interrupt from a different thread
    /// three milliseconds later flushed them. The operator heard none. Which
    /// message survived depended on which thread happened to carry the flag —
    /// a race, not a policy.
    ///
    /// The fix is a policy: **Interrupt jumps the queue, it does not burn
    /// it.** Every queued utterance that reaches the reader is remembered
    /// here with a deliberately generous estimate of when it will have been
    /// spoken. A non-Urgent interrupt re-queues everything still believed
    /// unspoken, in order, behind itself. <see cref="SpeechIntent.Urgent"/>
    /// alone discards — that is its entire meaning — and the operator's own
    /// Silence clears the ledger too, because resurrecting speech someone
    /// just shut up would defy them.
    ///
    /// **Why estimates, not truth.** Asking the backend whether it is still
    /// speaking is a per-backend feature bit; a design that polls it works
    /// under one screen reader and silently stalls under another (the MinGap
    /// comment below makes the same argument). And the reader speaks traffic
    /// we never see — its own focus and window announcements — so our text
    /// often starts LATER than any model predicts. The estimate therefore
    /// errs long on purpose: over-protection at worst repeats something the
    /// operator already heard, under-protection silently destroys something
    /// they never did. Those costs are not symmetric.
    ///
    /// Instance-based with an injected <see cref="ISpeechClock"/> so every
    /// timing constant here is testable exactly — advance a manual clock,
    /// assert precisely one utterance, assert when. Production wiring lives
    /// in <c>ScreenReaderOutput</c>, which remains the only public surface.
    /// </summary>
    internal sealed class SpeechArbiter
    {
        // ── Latest (lead-then-settle) constants ──

        /// <summary>Quiet period after the LAST change before a Latest utterance speaks.</summary>
        ///
        /// A debounce, not a throttle - the timer restarts on every new value,
        /// so a sweep speaks once when the operator stops rather than at a
        /// fixed cadence while they are still moving.
        ///
        /// The first attempt got this wrong and it was audible: a fixed 120 ms
        /// window that did NOT restart fired every 120 ms during a hold, each
        /// utterance cutting off the last after about one phoneme. Reported
        /// 2026-08-18 as "r r r r r RF gain 5". The comment justifying it
        /// claimed restarting would "defer the announcement forever" - which
        /// cannot happen, because a sweep ends when a finger comes off a key.
        internal const int CoalesceMs = 300;

        /// <summary>
        /// How long after an utterance a key is still considered "sweeping",
        /// so the next value coalesces instead of speaking immediately.
        ///
        /// Must comfortably exceed the Windows key-repeat INITIAL delay, which
        /// defaults to around half a second. That delay is the whole reason a
        /// plain debounce clicks: press and hold, and the gap before the repeat
        /// burst arrives is longer than any debounce short enough to feel
        /// responsive - so the first press speaks, the burst speaks again, and
        /// the second cuts off the first.
        /// </summary>
        internal const int SweepWindowMs = 1200;

        /// <summary>
        /// Minimum spacing between two utterances WE emit for the same key.
        ///
        /// Every Latest emission interrupts, which is right when it supersedes
        /// a stale value and wrong when the thing it interrupts is us. A value
        /// announcement takes roughly a second to speak, so a settle coming due
        /// shortly after the lead cut the lead off mid-word - heard as clicks
        /// and ticks while sweeping.
        ///
        /// Measured from the trace on 2026-08-18: "TX Power 87" at 26.978 s,
        /// "TX Power 86" at 28.256 s. 1.28 seconds apart, against an utterance
        /// about 1.2 seconds long, landing exactly on the tail of the first.
        ///
        /// Tuning the sweep window cannot fix this - the window and the
        /// utterance are the same order of magnitude, so any setting trades
        /// clicks for lag. A floor on the gap fixes it directly: a settle that
        /// comes due too soon WAITS rather than cutting in, and speaks a moment
        /// later with the same information.
        ///
        /// Deliberately a fixed estimate rather than asking the backend whether
        /// it is still speaking: is-speaking is a per-backend feature bit, so a
        /// design that polls it works under one screen reader and silently
        /// stalls under another.
        /// </summary>
        internal const int MinGapMs = 1200;

        // A "speak anyway after N ms" ceiling used to live here, so a long hold
        // got periodic feedback rather than silence. It was REMOVED on
        // 2026-08-18 because it did not work by ear: a value announcement takes
        // longer to speak than the ceiling allowed, so each periodic utterance
        // was cut off by the next one, producing the clicks and ticks the
        // operator reported while sweeping.
        //
        // The choice is between silence during a hold and speech that is
        // audibly chopped. Silence is better: the operator is holding a key
        // deliberately and knows the value is moving, whereas a click carries
        // no information at all and sounds like a fault.
        //
        // If periodic feedback is wanted later it must be SHORT enough to
        // finish - the bare number rather than the whole phrase - not the full
        // announcement fired more often.

        // ── Believed-pending ledger constants ──

        /// <summary>
        /// Per-character speaking-time estimate for the ledger. Deliberately
        /// GENEROUS where <c>ScreenReaderOutput</c>'s SpeakAndWait constant
        /// (50 ms) is deliberately short: a wait that runs long holds the app,
        /// but a protection window that runs short silently destroys speech.
        /// 80 ms/char is a slow-but-real speaking rate; a fast-rate operator
        /// finishes sooner, and the worst case for them is hearing a repeat of
        /// something already heard — recoverable with the silence key, unlike
        /// the silent loss this replaces.
        /// </summary>
        internal const int SalvageMsPerCharacter = 80;

        /// <summary>
        /// Floor per utterance. Even one short word occupies the reader for a
        /// beat, and readers pause between queue items; also absorbs some of
        /// the reader's own traffic (focus announcements) that delays our text
        /// in ways no model of only-our-traffic can see. The 2026-08-25
        /// capture is the proof such delays are real: "Disconnected" was still
        /// unspoken 670 ms after we handed it over.
        /// </summary>
        internal const int SalvageMinMs = 800;

        /// <summary>
        /// Ceiling per utterance, so one enormous paragraph cannot hold the
        /// ledger open for a minute and turn every later interrupt into a
        /// replay of ancient history.
        /// </summary>
        internal const int SalvageCapMs = 15000;

        /// <summary>
        /// Ledger size cap. Overflow drops the OLDEST entry — the one most
        /// likely to have actually been heard. Purely defensive; a queue this
        /// deep is itself a bug the #197 transcript rule exists to catch.
        /// </summary>
        private const int LedgerCap = 16;

        // ── State ──

        private sealed class PendingUtterance
        {
            public string Message = string.Empty;
            public VerbosityLevel Level;

            /// <summary>See the repeatWhileHeld parameter on ScreenReaderOutput.Speak.</summary>
            public bool RepeatWhileHeld;
            /// <summary>Call site of the newest value, for the transcript.</summary>
            public string? Origin;
            public ISpeechTimer? Timer;
        }

        private sealed class BelievedQueued
        {
            public string Message = string.Empty;
            public SpeechIntent? Intent;
            public VerbosityLevel? Level;
            public string? Origin;
            /// <summary>When the reader is estimated to have finished saying it.</summary>
            public DateTime EstFinishUtc;
        }

        private readonly Dictionary<string, PendingUtterance> _pending =
            new Dictionary<string, PendingUtterance>(StringComparer.Ordinal);

        /// <summary>Per key: what was last spoken, and when.</summary>
        private readonly Dictionary<string, (string Message, DateTime At)> _lastByKey =
            new Dictionary<string, (string, DateTime)>(StringComparer.Ordinal);

        /// <summary>
        /// Queued utterances handed to the reader and believed not yet fully
        /// spoken — the material a non-Urgent interrupt must re-queue instead
        /// of destroy. Interrupting emissions never enter it: they chose
        /// immediacy over protection, and replaying a superseded value (a cut
        /// Latest lead, say) would be actively wrong.
        /// </summary>
        private readonly List<BelievedQueued> _believedQueued = new List<BelievedQueued>();

        /// <summary>
        /// When the reader is estimated to fall silent, counting only our own
        /// traffic. Queued emissions stack onto it; an interrupt that reached
        /// the reader resets it (everything before it is gone).
        /// </summary>
        private DateTime _readerBusyUntilUtc = DateTime.MinValue;

        private readonly object _lock = new object();

        private readonly ISpeechClock _clock;
        private readonly Func<VerbosityLevel> _verbosity;
        private readonly SpeechSink _sink;
        private readonly Action _silenceBackend;
        private readonly Action<string, VerbosityLevel, SpeechIntent?, string?> _recordGated;

        /// <param name="clock">Time source. Inject a manual clock to test.</param>
        /// <param name="verbosity">Read at flush time — the setting can move while a value is pending.</param>
        /// <param name="sink">Where decided utterances go. See <see cref="SpeechSink"/>.</param>
        /// <param name="silenceBackend">Cut current speech now. Used by Urgent only.</param>
        /// <param name="recordGated">Transcript record for "fired but the verbosity filter dropped it".</param>
        public SpeechArbiter(
            ISpeechClock clock,
            Func<VerbosityLevel> verbosity,
            SpeechSink sink,
            Action silenceBackend,
            Action<string, VerbosityLevel, SpeechIntent?, string?> recordGated)
        {
            _clock = clock;
            _verbosity = verbosity;
            _sink = sink;
            _silenceBackend = silenceBackend;
            _recordGated = recordGated;
        }

        /// <summary>
        /// Estimated milliseconds the reader spends saying <paramref name="message"/>.
        /// Shared with the #197 transcript queue-depth rule so the live ledger
        /// and the offline analysis cannot drift apart.
        /// </summary>
        internal static int EstimateSpokenMs(string message) =>
            Math.Min(SalvageCapMs, Math.Max(SalvageMinMs, message.Length * SalvageMsPerCharacter));

        /// <summary>
        /// Emit an utterance now — the funnel for Queue, Interrupt, Urgent and
        /// the legacy bool overloads. Ledger accounting and interrupt salvage
        /// happen here, so no overload can bypass the protection policy.
        /// </summary>
        public void Emit(string message, bool interrupt,
            SpeechIntent? intent, VerbosityLevel? level, string? origin)
        {
            lock (_lock)
            {
                EmitLocked(message, interrupt, intent, level, origin);
            }
        }

        /// <summary>
        /// Lead, then settle.
        ///
        /// The FIRST value for a key speaks immediately, so a single deliberate
        /// press is instant. Anything arriving while that key is still sweeping
        /// is coalesced and spoken once the operator stops.
        ///
        /// **Why not a plain debounce.** Windows key repeat waits about half a
        /// second before the burst begins - longer than any debounce short
        /// enough to feel responsive. So a plain debounce speaks on the first
        /// press, speaks again after the burst, and the second cuts off the
        /// first. That was heard as clicks and ticks while sweeping a value on
        /// 2026-08-18. The tuning code had already solved it this way, by hand,
        /// and worked; this brings the same shape into the shared mechanism
        /// instead of leaving it as a sixth private copy.
        ///
        /// Coalescing has to happen before emission: once text reaches a screen
        /// reader we cannot take it back.
        /// </summary>
        public void Latest(string key, string message, VerbosityLevel level,
            bool repeatWhileHeld, string? origin)
        {
            lock (_lock)
            {
                if (_pending.TryGetValue(key, out var existing))
                {
                    existing.Message = message;
                    existing.Level = level;
                    existing.RepeatWhileHeld = repeatWhileHeld;
                    existing.Origin = origin;

                    // A repeating entry must NOT have its timer pushed out by
                    // each new keypress: the operator is holding the key, so
                    // restarting the wait would defer the announcement for
                    // exactly as long as they need to hear it.
                    if (repeatWhileHeld) return;

                    try
                    {
                        existing.Timer?.Change(CoalesceMs);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Raced with its own flush; the next value starts a
                        // fresh entry, so there is nothing to repair.
                    }
                    return;
                }

                // Not sweeping: speak now. This is the single deliberate press,
                // and making it wait is the difference between a control that
                // answers and one that feels sticky.
                var now = _clock.UtcNow;
                bool sweeping =
                    _lastByKey.TryGetValue(key, out var last)
                    && (now - last.At).TotalMilliseconds < SweepWindowMs;

                if (!sweeping && RemainingGapMsLocked(key) == 0)
                {
                    _lastByKey[key] = (message, now);
                    EmitLocked(message, interrupt: true, SpeechIntent.Latest, level, origin);
                    return;
                }

                // Either mid-sweep, or too soon after our own last utterance to
                // lead without clipping it. Both cases coalesce.

                var entry = new PendingUtterance
                {
                    Message = message,
                    Level = level,
                    RepeatWhileHeld = repeatWhileHeld,
                    Origin = origin,
                };
                _pending[key] = entry;
                entry.Timer = _clock.StartTimer(CoalesceMs, () => FlushCoalesced(key));
            }
        }

        /// <summary>
        /// Transmit safety: cut what is speaking AND drop what is queued —
        /// ours and the reader's — so nothing stale can play on top of the
        /// warning. The one intent for which discard is the point.
        /// </summary>
        public void Urgent(string message, VerbosityLevel level, string? origin)
        {
            lock (_lock)
            {
                DiscardAllLocked();
                try { _silenceBackend(); } catch { }
                EmitLocked(message, interrupt: true, SpeechIntent.Urgent, level, origin);
            }
        }

        /// <summary>
        /// The operator (or a window transition) explicitly silenced speech.
        /// Forget the believed backlog: resurrecting utterances someone just
        /// shut up would defy them. Pending coalesced values are deliberately
        /// left alone — a settle that fires afterwards carries the CURRENT
        /// value of a control the operator was actively sweeping, which is not
        /// the chatter they silenced.
        /// </summary>
        public void OnSilenced()
        {
            lock (_lock)
            {
                _believedQueued.Clear();
                _readerBusyUntilUtc = DateTime.MinValue;
            }
        }

        /// <summary>Drop all pending state. Shutdown, and Urgent's first step.</summary>
        public void DiscardAll()
        {
            lock (_lock)
            {
                DiscardAllLocked();
            }
        }

        // ── Internals ──

        private void EmitLocked(string message, bool interrupt,
            SpeechIntent? intent, VerbosityLevel? level, string? origin)
        {
            var now = _clock.UtcNow;
            PruneLedgerLocked(now);

            if (!interrupt)
            {
                bool reached = _sink(message, false, intent, level, origin, salvaged: false);
                if (reached) LedgerAddLocked(message, intent, level, origin, now);
                return;
            }

            bool sounded = _sink(message, true, intent, level, origin, salvaged: false);
            if (!sounded)
            {
                // Suppressed or no backend: the reader never saw the cancel,
                // so its queue — and our ledger — stand untouched.
                return;
            }

            // The interrupt flushed the reader. Everything believed unspoken
            // is gone from its queue; re-queue it, in order, behind the
            // interrupter. Urgent skips this on purpose (ledger already
            // cleared by DiscardAllLocked, but the check keeps the policy
            // explicit rather than an artifact of call order).
            _readerBusyUntilUtc = now.AddMilliseconds(EstimateSpokenMs(message));

            if (intent == SpeechIntent.Urgent || _believedQueued.Count == 0)
            {
                _believedQueued.Clear();
                return;
            }

            var salvage = _believedQueued.ToArray();
            _believedQueued.Clear();
            foreach (var s in salvage)
            {
                bool requeued = _sink(s.Message, false, s.Intent, s.Level, s.Origin, salvaged: true);
                // Re-enter the ledger so a SECOND interrupt cannot destroy
                // what the first one already had to salvage.
                if (requeued) LedgerAddLocked(s.Message, s.Intent, s.Level, s.Origin, _clock.UtcNow);
            }
        }

        private void LedgerAddLocked(string message,
            SpeechIntent? intent, VerbosityLevel? level, string? origin, DateTime now)
        {
            var start = _readerBusyUntilUtc > now ? _readerBusyUntilUtc : now;
            var finish = start.AddMilliseconds(EstimateSpokenMs(message));
            _readerBusyUntilUtc = finish;

            if (_believedQueued.Count >= LedgerCap) _believedQueued.RemoveAt(0);
            _believedQueued.Add(new BelievedQueued
            {
                Message = message,
                Intent = intent,
                Level = level,
                Origin = origin,
                EstFinishUtc = finish,
            });
        }

        private void PruneLedgerLocked(DateTime now)
        {
            // Estimated-finished utterances leave the ledger; salvaging them
            // would repeat speech the operator (probably) heard.
            _believedQueued.RemoveAll(e => e.EstFinishUtc <= now);
        }

        private void FlushCoalesced(string key)
        {
            lock (_lock)
            {
                if (!_pending.TryGetValue(key, out var entry)) return;
                _pending.Remove(key);

                // Nothing new to say. Skipping matters: on a two- or three-step
                // sweep the settle would otherwise arrive while the lead
                // utterance is still speaking and cut it off to repeat a value
                // the operator has already heard.
                if (!entry.RepeatWhileHeld
                    && _lastByKey.TryGetValue(key, out var last)
                    && string.Equals(last.Message, entry.Message, StringComparison.Ordinal))
                {
                    entry.Timer?.Dispose();
                    return;
                }

                // Too soon after our own last utterance for this key: speaking
                // now would cut it off mid-word. Put the entry back and wait
                // out the remainder - the information is unchanged, only its
                // timing moves.
                int wait = RemainingGapMsLocked(key);
                if (wait > 0)
                {
                    _pending[key] = entry;
                    try
                    {
                        entry.Timer?.Change(wait);
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        _pending.Remove(key);
                        // Fall through and speak; a disposed timer cannot be
                        // rescheduled, and losing the value entirely is worse
                        // than a clipped one.
                    }
                }

                _lastByKey[key] = (entry.Message, _clock.UtcNow);
                entry.Timer?.Dispose();

                if ((int)entry.Level <= (int)_verbosity())
                {
                    EmitLocked(entry.Message, interrupt: true, SpeechIntent.Latest,
                        entry.Level, entry.Origin);
                }
                else
                {
                    // The verbosity setting moved while this value was pending.
                    _recordGated(entry.Message, entry.Level, SpeechIntent.Latest, entry.Origin);
                }
            }
        }

        /// <summary>
        /// True when speaking for this key right now would cut off our own
        /// previous utterance; the caller should wait out the remainder.
        /// Returns the milliseconds still to wait, or 0 when clear.
        /// </summary>
        private int RemainingGapMsLocked(string key)
        {
            if (!_lastByKey.TryGetValue(key, out var last)) return 0;
            var elapsed = (_clock.UtcNow - last.At).TotalMilliseconds;
            var remaining = MinGapMs - elapsed;
            return remaining <= 0 ? 0 : (int)Math.Ceiling(remaining);
        }

        private void DiscardAllLocked()
        {
            foreach (var entry in _pending.Values) entry.Timer?.Dispose();
            _pending.Clear();

            // Forget what was last spoken as well, so the next value after
            // an urgent warning always speaks rather than being suppressed
            // as a duplicate of something the flush just discarded.
            _lastByKey.Clear();

            _believedQueued.Clear();
            _readerBusyUntilUtc = DateTime.MinValue;
        }
    }
}
