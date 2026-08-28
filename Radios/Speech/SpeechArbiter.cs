#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using JJTrace;

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
    /// **And the rescue is bounded.** A salvaged utterance re-enters the
    /// ledger, which is right — a second interrupt must not destroy what the
    /// first one had to rescue — but until 2026-08-27 it re-entered with a
    /// fresh lease and no memory of having been rescued at all, so it renewed
    /// its own youth and extended the very window that justified rescuing it
    /// again. Nine keypresses produced ten salvages of one sentence, arriving
    /// later each time. Every entry now carries the moment it FIRST reached the
    /// reader and how many times it has been rescued, and is dropped past
    /// either bound. Stale speech is worse than silence: re-speaking "Detailed
    /// capture started" a minute later does not merely annoy, it says
    /// "started" about something that started long ago.
    ///
    /// **Why estimates, not truth.** Asking the backend whether it is still
    /// speaking is a per-backend feature bit; a design that polls it works
    /// under one screen reader and silently stalls under another (the
    /// anti-clip gap comment below makes the same argument). And the reader
    /// speaks traffic we never see — its own focus and window announcements —
    /// so our text often starts LATER than any model predicts. The estimate
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

        // ── The anti-clip gap: minimum spacing between two utterances WE
        //    emit for the same key ──
        //
        // Every Latest emission interrupts, which is right when it supersedes
        // a stale value and wrong when the thing it interrupts is us. A value
        // announcement takes roughly a second to speak, so a settle coming due
        // shortly after the lead cut the lead off mid-word - heard as clicks
        // and ticks while sweeping.
        //
        // Measured from the trace on 2026-08-18: "TX Power 87" at 26.978 s,
        // "TX Power 86" at 28.256 s. 1.28 seconds apart, against an utterance
        // about 1.2 seconds long, landing exactly on the tail of the first.
        //
        // Tuning the sweep window cannot fix this - the window and the
        // utterance are the same order of magnitude, so any setting trades
        // clicks for lag. A floor on the gap fixes it directly: a settle that
        // comes due too soon WAITS rather than cutting in, and speaks a moment
        // later with the same information.
        //
        // Deliberately a fixed estimate rather than asking the backend whether
        // it is still speaking: is-speaking is a per-backend feature bit, so a
        // design that polls it works under one screen reader and silently
        // stalls under another.
        //
        // **This was one flat constant, MinGapMs = 1200, until 2026-08-27.**
        // A flat floor sized for the WORST utterance charges every utterance
        // the worst utterance's price: "S 3" waited as long as a full sentence
        // for no reason at all. It also happened to equal SweepWindowMs, which
        // read as one number doing two jobs - "when does this stop counting as
        // a sweep" and "when may I speak again" are different questions and
        // their agreeing on 1200 was a coincidence, not a design.
        //
        // The gap is now derived from what was actually spoken, which is what
        // the floor was always trying to approximate. The ceiling is the old
        // constant, deliberately: this change may only make the gap SHORTER
        // than it used to be, never longer, so it cannot introduce a new lag
        // anywhere, and the 2026-08-18 case above still gets its full 1200.

        /// <summary>
        /// Speaking-rate estimate for the anti-clip gap. NOT the ledger's
        /// <see cref="SalvageMsPerCharacter"/>, and the difference is not an
        /// oversight.
        ///
        /// The ledger protects mostly PROSE — connect messages, warnings — for
        /// which 80 ms/char is a generous, err-long rate. The gap governs
        /// Latest keys, which are short NUMERIC readouts where characters
        /// expand into syllables: "87" is two characters and three syllables,
        /// "TX" is two characters and two spelled letters. The one measurement
        /// this class records is exactly that content — "TX Power 87", eleven
        /// characters, about 1.2 s — which is 110 ms/char, not 80. Applying
        /// the ledger's prose rate here would UNDER-estimate a readout and
        /// re-open the 2026-08-18 clipping regression.
        /// </summary>
        internal const int GapMsPerCharacter = 110;

        /// <summary>
        /// Floor on the derived gap. Character count is a poor proxy at the
        /// short end: "S 3" is three characters and takes far longer than
        /// 330 ms to say, because a letter and a digit are each a whole word.
        /// 700 ms covers the shortest real announcements ("Mute on",
        /// "Volume 5", an S-meter reading) without charging them the full
        /// sentence price.
        /// </summary>
        internal const int GapFloorMs = 700;

        /// <summary>
        /// Ceiling on the derived gap — the old flat MinGapMs, kept as the
        /// upper bound so this change is strictly a reduction. A value readout
        /// long enough to hit this is a value readout that genuinely takes
        /// that long to speak.
        /// </summary>
        internal const int GapCeilingMs = 1200;

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

        /// <summary>
        /// How many times one utterance may be salvaged before it is dropped.
        ///
        /// **The bound that stops the runaway.** A salvaged utterance re-enters
        /// the ledger so that a SECOND interrupt cannot destroy what the first
        /// one already had to rescue — which is right, and is why this is 2 and
        /// not 1. What was missing was any end to it: each re-entry took a
        /// FRESH lease and pushed <c>_readerBusyUntilUtc</c> further out, so the
        /// window that justified the next salvage was manufactured by the last
        /// one. Measured 2026-08-26: nine keypresses produced TEN salvages of
        /// one sentence, each arriving later than the last, and nothing in the
        /// mechanism would ever have stopped it.
        ///
        /// Two rescues is where protection stops being protection. An utterance
        /// that has been re-queued twice and still not been spoken is chasing a
        /// burst of interrupts it is not going to get ahead of, and by then the
        /// operator has heard two newer things instead.
        /// </summary>
        internal const int MaxSalvages = 2;

        /// <summary>
        /// Age bound, as a multiple of the utterance's OWN estimated duration,
        /// measured from FIRST emission rather than the latest re-queue.
        ///
        /// Measuring from the latest re-queue is precisely the defect: it lets
        /// an utterance renew its own youth. Measured from first emission it
        /// cannot, however many times it is rescued.
        ///
        /// Two of its own duration, because <see cref="EstimateSpokenMs"/> is
        /// already the generous err-long estimate — one multiple is merely
        /// "should have finished by now", and doubling it leaves room for the
        /// reader's own focus and window traffic to have delayed our text (the
        /// 2026-08-25 capture measured 670 ms of exactly that). Past two, the
        /// utterance is describing a moment that has gone. "Detailed capture
        /// started" re-spoken a minute later does not merely annoy, it LIES.
        /// </summary>
        internal const int SalvageAgeMultiple = 2;

        // ── State ──

        private sealed class PendingUtterance
        {
            public string Message = string.Empty;
            public VerbosityLevel Level;

            /// <summary>Sweeping a value, or asking a question. See <see cref="SpeechCoalesceKind"/>.</summary>
            public SpeechCoalesceKind Kind;
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

            /// <summary>
            /// When this utterance FIRST reached the reader. Never moves, however
            /// many times the entry is salvaged — that is the whole point: the
            /// age bound has to be measured against something a re-queue cannot
            /// renew. <see cref="EstFinishUtc"/> is renewed by design, so it is
            /// the wrong thing to age against.
            /// </summary>
            public DateTime FirstEmittedUtc;

            /// <summary>How many times an interrupt has already rescued this one.</summary>
            public int SalvageCount;
        }

        private readonly Dictionary<string, PendingUtterance> _pending =
            new Dictionary<string, PendingUtterance>(StringComparer.Ordinal);

        /// <summary>
        /// Per key: what was last spoken, when, and how long the next utterance
        /// for this key must wait so it does not cut that one off. The gap is
        /// stored rather than recomputed because it belongs to the message that
        /// was SPOKEN, and by the time the next one is due that message is gone
        /// from everywhere else.
        /// </summary>
        private readonly Dictionary<string, (string Message, DateTime At, int GapMs)> _lastByKey =
            new Dictionary<string, (string, DateTime, int)>(StringComparer.Ordinal);

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
        /// Estimated milliseconds the reader spends saying <paramref name="message"/>,
        /// for ledger protection. Deliberately NOT shared with the #197
        /// transcript queue-depth rule, whose estimate must err realistic
        /// where this one must err generous — see SpeechQueueDepthRule's
        /// class doc for the asymmetry argument.
        /// </summary>
        internal static int EstimateSpokenMs(string message) =>
            Math.Min(SalvageCapMs, Math.Max(SalvageMinMs, message.Length * SalvageMsPerCharacter));

        /// <summary>
        /// How long the next utterance for a key must wait after
        /// <paramref name="message"/> so it does not cut it off mid-word.
        ///
        /// Same shape as <see cref="EstimateSpokenMs"/> and deliberately NOT
        /// the same numbers — see <see cref="GapMsPerCharacter"/> for the
        /// rate and <see cref="GapFloorMs"/> for the floor. Two policies over
        /// one idea, which is the pattern this class already uses between the
        /// ledger and SpeechQueueDepthRule: the errors point in opposite
        /// directions, so one set of constants cannot serve both. Erring long
        /// in the ledger costs a repeat; erring long HERE costs an answer the
        /// operator asked for and did not get.
        /// </summary>
        internal static int AntiClipGapMs(string message) =>
            Math.Clamp(message.Length * GapMsPerCharacter, GapFloorMs, GapCeilingMs);

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
        ///
        /// **On <paramref name="kind"/> — #264, and the rule it encodes.**
        /// A key that asks a question is not a value that sweeps. The lead-then-
        /// settle policy below is right for a value in flight and wrong for a
        /// re-request, and until 2026-08-27 nothing here could tell them apart —
        /// so a second Ctrl+S inside <see cref="SweepWindowMs"/> was treated as
        /// sweeping and made to wait out a settle it had no reason to wait for.
        /// Measured at the radio: about half a second, on the one key whose job
        /// is to answer now.
        ///
        /// The classification lives at the CALL SITE because that is the only
        /// place that knows: a query key and a value-adjust key are different
        /// commands. Surveyed across every <c>coalesceKey</c> site on
        /// 2026-08-27 and re-checked here — the S-meter is the only
        /// <see cref="SpeechCoalesceKind.Query"/>; gain, volume, slice volume
        /// and the value field are all swept values that keep the settle,
        /// because the tail is genuinely the right answer there. If a new query
        /// key appears, it belongs on this side of the line.
        ///
        /// **This is a classification change and NOT a constant change.**
        /// Shortening <see cref="SweepWindowMs"/> would have produced the same
        /// measurement on Ctrl+S and degraded every sweep that constant exists
        /// for. The window is untouched.
        /// </summary>
        public void Latest(string key, string message, VerbosityLevel level,
            SpeechCoalesceKind kind, string? origin)
        {
            lock (_lock)
            {
                if (_pending.TryGetValue(key, out var existing))
                {
                    existing.Message = message;
                    existing.Level = level;
                    existing.Kind = kind;
                    existing.Origin = origin;

                    // A query must NOT have its timer pushed out by the next
                    // press: the operator is asking again, so restarting the
                    // wait defers the answer for exactly as long as they keep
                    // asking for it. For a swept value the push-out is the
                    // point — it is what makes a hold speak once, at the end.
                    if (kind == SpeechCoalesceKind.Query) return;

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
                    kind == SpeechCoalesceKind.Value
                    && _lastByKey.TryGetValue(key, out var last)
                    && (now - last.At).TotalMilliseconds < SweepWindowMs;

                int gapWait = RemainingGapMsLocked(key);

                if (!sweeping && gapWait == 0)
                {
                    _lastByKey[key] = (message, now, AntiClipGapMs(message));
                    EmitLocked(message, interrupt: true, SpeechIntent.Latest, level, origin);
                    return;
                }

                // Either mid-sweep, or too soon after our own last utterance to
                // lead without clipping it. Both cases coalesce.
                //
                // A QUERY only ever reaches here for the second reason, and its
                // wait is therefore the anti-clip gap itself — not the settle.
                // Arming the settle first and re-arming for the gap afterwards
                // (which is what a value does, in FlushCoalesced) would charge
                // an answer up to CoalesceMs it has no reason to pay.
                int dueMs = sweeping ? CoalesceMs : (kind == SpeechCoalesceKind.Query ? gapWait : CoalesceMs);

                var entry = new PendingUtterance
                {
                    Message = message,
                    Level = level,
                    Kind = kind,
                    Origin = origin,
                };
                _pending[key] = entry;
                entry.Timer = _clock.StartTimer(dueMs, () => FlushCoalesced(key));
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
                string? refusal = SalvageRefusalLocked(s, now);
                if (refusal != null)
                {
                    // A salvage that gives up SILENTLY is the same defect class
                    // as the one this bound exists to fix: speech that vanishes
                    // while the record says everything is fine. Say which bound
                    // was hit and what it was measured against.
                    Tracing.TraceLine(
                        $"SpeechArbiter: dropped a salvage ({refusal}) after "
                        + $"{s.SalvageCount} rescue(s), "
                        + $"{(int)(now - s.FirstEmittedUtc).TotalMilliseconds} ms after first "
                        + $"emission: '{s.Message}'",
                        TraceLevel.Warning);
                    continue;
                }

                bool requeued = _sink(s.Message, false, s.Intent, s.Level, s.Origin, salvaged: true);
                // Re-enter the ledger so a SECOND interrupt cannot destroy
                // what the first one already had to salvage — bounded, now, by
                // the count it carries with it.
                if (requeued)
                {
                    s.SalvageCount++;
                    LedgerEnterLocked(s, _clock.UtcNow);
                }
            }
        }

        /// <summary>
        /// Why this utterance may NOT be salvaged again, or null when it may.
        /// The returned phrase goes straight into the trace, so it names the
        /// bound and the measurement rather than merely reporting a refusal.
        /// </summary>
        private string? SalvageRefusalLocked(BelievedQueued entry, DateTime now)
        {
            if (entry.SalvageCount >= MaxSalvages)
                return $"salvage cap: already rescued {entry.SalvageCount} times, limit {MaxSalvages}";

            int ageMs = (int)(now - entry.FirstEmittedUtc).TotalMilliseconds;
            int boundMs = EstimateSpokenMs(entry.Message) * SalvageAgeMultiple;
            if (ageMs > boundMs)
                return $"stale: {ageMs} ms old against a {boundMs} ms bound";

            return null;
        }

        /// <summary>A first entry into the ledger: this is emission number one.</summary>
        private void LedgerAddLocked(string message,
            SpeechIntent? intent, VerbosityLevel? level, string? origin, DateTime now)
        {
            LedgerEnterLocked(new BelievedQueued
            {
                Message = message,
                Intent = intent,
                Level = level,
                Origin = origin,
                FirstEmittedUtc = now,
                SalvageCount = 0,
            }, now);
        }

        /// <summary>
        /// Put an entry into the ledger and stack its estimated speaking time
        /// onto the reader's believed busy-until.
        ///
        /// A re-entering salvage brings its own <c>FirstEmittedUtc</c> and
        /// <c>SalvageCount</c> with it. That is the fix for #273 in one line:
        /// the lease is renewed, as it must be — the reader really is going to
        /// be busy that long again — but the entry's AGE and its rescue count
        /// are not, so the thing that justifies the next rescue is no longer
        /// manufactured by the last one.
        /// </summary>
        private void LedgerEnterLocked(BelievedQueued entry, DateTime now)
        {
            var start = _readerBusyUntilUtc > now ? _readerBusyUntilUtc : now;
            var finish = start.AddMilliseconds(EstimateSpokenMs(entry.Message));
            _readerBusyUntilUtc = finish;
            entry.EstFinishUtc = finish;

            if (_believedQueued.Count >= LedgerCap) _believedQueued.RemoveAt(0);
            _believedQueued.Add(entry);
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
                //
                // A QUERY is exempt, and this is the other half of #264's rule:
                // press Ctrl+S twice on a steady signal and the second press
                // must say "S 7" again. The repetition IS the information —
                // it is how the operator learns the signal has not moved.
                // Dropping it meant a deliberate second press said nothing at
                // all, which is indistinguishable from the key being broken.
                if (entry.Kind != SpeechCoalesceKind.Query
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

                _lastByKey[key] = (entry.Message, _clock.UtcNow, AntiClipGapMs(entry.Message));
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
        ///
        /// The wait is the gap the PREVIOUS message earned, not a constant:
        /// a short readout is out of the way sooner and must not hold the key
        /// for as long as a sentence would.
        /// </summary>
        private int RemainingGapMsLocked(string key)
        {
            if (!_lastByKey.TryGetValue(key, out var last)) return 0;
            var elapsed = (_clock.UtcNow - last.At).TotalMilliseconds;
            var remaining = last.GapMs - elapsed;
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
