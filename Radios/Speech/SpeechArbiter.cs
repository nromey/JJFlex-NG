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
    /// **And a rescue is refused by SUPERSESSION, not by age (#503).** Until
    /// 2026-09-02 the age bound was twice the utterance's own word-count
    /// estimate, which made lifetime a function of length — and length runs
    /// the wrong way. Measured across 2026-09-01: 89 drops, 52 never re-spoken
    /// even once; "SWR 1.7" was binned 3.9 s after a tune against a 1.6 s
    /// bound, by the operator pressing Tune AGAIN because they had heard
    /// nothing. Nothing expires on its own clock here — an entry is judged
    /// only when the next interrupt arrives — so the retry a lost answer
    /// provokes was the very event that destroyed the answer. An emitter now
    /// declares what its utterance is ABOUT (<see cref="SpeechSubject"/>); a
    /// newer statement on the same subject, or an explicit
    /// <see cref="Supersede"/>, is what retires it, under an absolute
    /// <see cref="SalvageCeilingMs"/>. The word-count bound survives only for
    /// utterances that declared nothing, because for those there is no honest
    /// alternative — see <see cref="SalvageAgeMultiple"/>.
    ///
    /// **And the train is HELD through a settle window before it is handed
    /// over (#507).** Until 2026-09-02 the backlog was re-queued inside the
    /// interrupt's own call, which put it immediately behind the interrupter
    /// — and the interrupter is the LEAD of an action whose real answer is
    /// queued a moment later by the same handler. So at 202636 @65323 the
    /// operator pressed Tune, heard "Tune on", then a sixty-character warning
    /// about profile saving, then "Tune Power 5", and only then did the radio
    /// act; and at 213210 @4015266 every Tab press re-delivered the same
    /// stale warning, each press spending one of its two rescues. The queue
    /// order was correct and the defect was placement. Supersession (#503)
    /// removes the WORTHLESS occupants of that gap and cannot remove the true
    /// ones: the receipt was keyed, unsuperseded and still worth hearing. So
    /// the survivors of an interrupt now wait in a held set for
    /// <see cref="SalvageSettleMs"/>; anything queued meanwhile goes to the
    /// reader at once, ahead of them; a further interrupt inside the window
    /// keeps them held without spending a rescue; and the release hands them
    /// over behind the action's own follow-ups. HOLD, never discard —
    /// discarding is "an interrupt burns the queue" in a new coat.
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
        ///
        /// **Applies only to utterances that declared no subject (#503).** A
        /// bound derived from word count makes lifetime a function of LENGTH,
        /// and length runs the wrong way: seven-character state facts like
        /// "SWR 1.7" got 1.6 s while a 300-character courtesy got thirty.
        /// A keyed utterance is instead retired by SUPERSESSION — something
        /// newer on the same subject — bounded by <see cref="SalvageCeilingMs"/>.
        /// The word-count bound stays for unkeyed utterances because for them
        /// the arbiter cannot know what would supersede them, and keeping
        /// everything for fifteen seconds would resurrect stale toggles and
        /// progress chatter. See <see cref="SpeechSubject"/>.
        /// </summary>
        internal const int SalvageAgeMultiple = 2;

        /// <summary>
        /// Absolute lifetime of any ledger entry, keyed or not, measured from
        /// first emission. Supersession alone would let a subject nobody
        /// revisits live forever — a "PC audio on." that nothing ever covers
        /// would still be rescued a minute later, describing a moment long
        /// gone. Fifteen seconds is also <see cref="SalvageCapMs"/>, and the
        /// equality is deliberate: the longest anything is estimated to take
        /// to say is also the longest anything may wait to be said about
        /// "now". The old policy let a long paragraph live twice that.
        /// </summary>
        internal const int SalvageCeilingMs = 15000;

        // ── The salvage settle window (#507) ──

        /// <summary>
        /// How long an interrupt's salvage train is held before it is handed
        /// to the reader, so that the interrupting action's OWN follow-ups
        /// go first. Measured from the LAST interrupt: a further interrupt
        /// inside the window re-arms it and keeps the train held.
        ///
        /// **Derived from the 2026-09-01 captures, not chosen.** Three
        /// measurements bracket it, and the bracket is pinned in
        /// SalvageSettleWindowTests so the number cannot drift out of it
        /// without a test saying so.
        ///
        /// **What must fit inside: an action's own follow-ups.** Across the
        /// six Verbose captures, 18 of the 24 queued follow-ups that belong
        /// to the action whose lead interrupted were handed over 0 to 2 ms
        /// after it — "SWR 1.1" behind "Tune off", seven times; the discovery
        /// line behind the Home arrival — three at 48 to 50 ms (the slice
        /// census and the receipt behind "Slice A, first slice", from a
        /// worker thread), and the slowest at 209 and 264 ms ("PC audio on"
        /// behind the Home arrival, from the audio thread). Anything past
        /// 300 ms already catches every follow-up seen.
        ///
        /// **What it is also asked to absorb: a burst of deliberate presses**,
        /// so the train is handed over once at the end rather than once per
        /// press with each hand-over spending a rescue. Measured: a Left-arrow
        /// walk across Home at 176 to 264 ms per press, and five Tab bursts at
        /// 408 to 552 ms per press — the #507 case is two Tabs 504 ms apart
        /// consuming both rescues (213210 @4015266, @4015770). So the window
        /// must exceed 552.
        ///
        /// **What bounds it above: the reader must still be speaking when the
        /// train arrives**, or the hold becomes an audible pause — which is
        /// exactly "a delay the operator feels". <see cref="GapFloorMs"/> is
        /// this class's own floor on how long the reader is busy with its
        /// SHORTEST utterance, the err-short estimate the anti-clip gap already
        /// stakes clipping on; releasing inside it means the train queues
        /// behind an utterance still being spoken, and the hand-over is
        /// inaudible by the same estimate. The shortest SEPARATE phase of one
        /// operation — "Disconnected from …" behind "Disconnecting from …" at
        /// +1,097 ms, "Connected to K5NER. Waiting for slice..." behind the
        /// connect line at +1,914 ms — lies well beyond the floor and correctly
        /// falls outside the window: those are acts of their own, and the
        /// backlog belongs between them.
        ///
        /// 600 sits inside that bracket: 48 ms above the slowest measured
        /// burst press, 100 ms below the floor. The responsive path is not
        /// touched by it at all — a lead, a settle, a query's answer and every
        /// queued follow-up reach the reader at exactly the instants they did
        /// before; only the backlog moves, and it moves later.
        /// </summary>
        internal const int SalvageSettleMs = 600;

        // ── State ──

        private sealed class PendingUtterance
        {
            public string Message = string.Empty;
            public VerbosityLevel Level;

            /// <summary>Sweeping a value, or asking a question. See <see cref="SpeechCoalesceKind"/>.</summary>
            public SpeechCoalesceKind Kind;
            /// <summary>Call site of the newest value, for the transcript.</summary>
            public string? Origin;
            /// <summary>The ledger subject a flushed value carries — its coalesce key, unless the caller said otherwise.</summary>
            public string? Subject;
            public ISpeechTimer? Timer;
        }

        private sealed class BelievedQueued
        {
            public string Message = string.Empty;
            public SpeechIntent? Intent;
            public VerbosityLevel? Level;
            public string? Origin;

            /// <summary>
            /// What this utterance is about, as declared by its emitter — see
            /// <see cref="SpeechSubject"/>. Null when the emitter declared
            /// nothing, in which case only the word-count bound and the
            /// ceiling can retire it.
            /// </summary>
            public string? Subject;

            /// <summary>
            /// Set when something newer covered this entry's subject: the
            /// newer message, quoted, or an emitter's stated reason. A
            /// superseded entry is never rescued, and the drop trace names
            /// this so the record says what made the words worthless.
            /// </summary>
            public string? SupersededBy;
            public string? SupersededByOrigin;
            public DateTime SupersededAtUtc;

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

            /// <summary>
            /// How many times a release has handed this one back to the
            /// reader. Being HELD is not a rescue: a burst of interrupts
            /// inside one settle window keeps the entry held and moves this
            /// not at all, because nothing was handed over and so nothing was
            /// rescued (#507). It counts hand-overs, which is what the cap
            /// was always bounding — repeats the operator may actually hear.
            /// </summary>
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

        /// <summary>
        /// The salvage train: entries an interrupt rescued from the ledger
        /// and has not yet handed to the reader (#507). Not in
        /// <see cref="_believedQueued"/>, because the reader does not have
        /// them; not spoken, because the settle window has not closed. Kept
        /// in arrival order, which is the order the reader had them in: the
        /// ledger is ordered by entry (a rescued entry re-enters behind what
        /// was queued while it waited, exactly as it re-entered the reader's
        /// queue), and a later interrupt in the same window only ever
        /// appends newer material behind what was already held.
        /// </summary>
        private readonly List<BelievedQueued> _held = new List<BelievedQueued>();

        /// <summary>The settle timer for the current hold; null when nothing is held.</summary>
        private ISpeechTimer? _holdTimer;

        /// <summary>
        /// Bumped every time the hold is armed, re-armed or ended. A release
        /// callback carries the generation it was armed with; one that
        /// arrives carrying an older number was overtaken — re-armed by a
        /// later interrupt, or cleared — and its window is not the one that
        /// closed. This is what makes "a timer fired while the lock was held
        /// by the interrupt that re-armed it" a no-op rather than an early
        /// release.
        /// </summary>
        private int _holdGeneration;

        /// <summary>When the CURRENT hold began — the first interrupt of a burst, not the latest.</summary>
        private DateTime _holdStartedUtc;

        /// <summary>The interrupter the hold is currently armed behind, for the trace.</summary>
        private string _holdBehind = string.Empty;

        /// <summary>How many interrupts have kept the current hold held — a burst's size.</summary>
        private int _holdInterrupts;

        /// <summary>
        /// Queued utterances the reader was given while the hold was open —
        /// the action's own follow-ups, which is what the hold exists to let
        /// through first. Named in the release trace so the record says not
        /// only that a hold happened and how long, but what went ahead.
        /// </summary>
        private readonly List<string> _wentFirst = new List<string>();

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
        /// <param name="subject">
        /// What the utterance is about (<see cref="SpeechSubject"/>), or null
        /// when the emitter declares nothing. A subject is what lets a later
        /// announcement retire this one by covering it, instead of a timer
        /// derived from its word count.
        /// </param>
        /// <param name="additive">
        /// True when this utterance ADDS to its subject rather than restating
        /// it — the "5" echoed after a "1" while a value is being typed. An
        /// additive utterance can be retired by a later restating one, or by
        /// <see cref="Supersede"/>, but it retires nothing itself: interrupted
        /// mid-entry, the operator must hear "1, 5" again, not a lone "5"
        /// over a field that reads 15. Almost everything is a restatement,
        /// so this defaults to false.
        /// </param>
        public void Emit(string message, bool interrupt,
            SpeechIntent? intent, VerbosityLevel? level, string? origin,
            string? subject = null, bool additive = false)
        {
            lock (_lock)
            {
                EmitLocked(message, interrupt, intent, level, origin, subject, additive);
            }
        }

        /// <summary>
        /// The emitter declares that <paramref name="subject"/> is covered:
        /// the operation it narrated has ended, the state it described has
        /// been replaced by something spoken elsewhere. Anything still
        /// believed unheard on that subject will not be rescued by the next
        /// interrupt, and the drop trace will say why in the caller's words.
        ///
        /// This exists because supersession is not always an utterance. A
        /// progress voice's last "still looking" is made worthless by the
        /// dialog that answers it — and that dialog's title is not a progress
        /// line, so it cannot carry the subject itself.
        /// </summary>
        /// <param name="by">
        /// Plain prose naming what covered the subject, as it should read in
        /// the trace after "superseded … by".
        /// </param>
        public void Supersede(string subject, string by, string? origin)
        {
            lock (_lock)
            {
                MarkSupersededLocked(subject, by, origin, _clock.UtcNow);
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
        /// <param name="subject">
        /// Ledger subject for the emission. Defaults to <paramref name="key"/>:
        /// a coalesce key already says "utterances sharing this replace one
        /// another", which is exactly what a subject says, one stage later.
        /// So a sweep over a field supersedes that field's queued typed value
        /// without anybody having to say so twice.
        /// </param>
        public void Latest(string key, string message, VerbosityLevel level,
            SpeechCoalesceKind kind, string? origin, string? subject = null)
        {
            subject ??= key;
            lock (_lock)
            {
                if (_pending.TryGetValue(key, out var existing))
                {
                    existing.Message = message;
                    existing.Level = level;
                    existing.Kind = kind;
                    existing.Origin = origin;
                    existing.Subject = subject;

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
                    EmitLocked(message, interrupt: true, SpeechIntent.Latest, level, origin, subject);
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
                    Subject = subject,
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
                DiscardAllLocked("an urgent warning discards everything queued");
                try { _silenceBackend(); } catch { }
                EmitLocked(message, interrupt: true, SpeechIntent.Urgent, level, origin, subject: null);
            }
        }

        /// <summary>
        /// The operator (or a window transition) explicitly silenced speech.
        /// Forget the believed backlog: resurrecting utterances someone just
        /// shut up would defy them. Pending coalesced values are deliberately
        /// left alone — a settle that fires afterwards carries the CURRENT
        /// value of a control the operator was actively sweeping, which is not
        /// the chatter they silenced. A train still held goes too, and the
        /// trace says so: it was the same backlog, one stage further from the
        /// reader.
        /// </summary>
        public void OnSilenced()
        {
            lock (_lock)
            {
                _believedQueued.Clear();
                _readerBusyUntilUtc = DateTime.MinValue;
                EndHoldLocked("the operator silenced speech");
            }
        }

        /// <summary>Drop all pending state. Shutdown, and Urgent's first step.</summary>
        public void DiscardAll()
        {
            lock (_lock)
            {
                DiscardAllLocked("all speech state discarded");
            }
        }

        // ── Internals ──

        private void EmitLocked(string message, bool interrupt,
            SpeechIntent? intent, VerbosityLevel? level, string? origin, string? subject,
            bool additive = false)
        {
            var now = _clock.UtcNow;
            PruneLedgerLocked(now);

            if (!interrupt)
            {
                bool reached = _sink(message, false, intent, level, origin, salvaged: false);
                if (reached)
                {
                    // A newer statement on the same subject reached the reader.
                    // Whatever earlier statement is still believed unheard is
                    // no longer worth rescuing: the reader will still say it in
                    // turn (text cannot be taken back), but an interrupt must
                    // not resurrect it. Marked BEFORE this one enters, so an
                    // utterance never supersedes itself. An ADDITIVE utterance
                    // marks nothing — it extends its subject, it does not
                    // restate it.
                    if (!additive) MarkSupersededLocked(subject, $"'{message}'", origin, now);
                    LedgerAddLocked(message, intent, level, origin, subject, now);

                    // Given to the reader while a train is held: this is one
                    // of the follow-ups the hold exists to let through first,
                    // and the release trace will name it.
                    if (_holdTimer != null) _wentFirst.Add(message);
                }
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
            // is gone from its queue and must be re-queued, in order, behind
            // the interrupter — but NOT in this call (#507). The interrupter
            // is the lead of an action whose own follow-ups are about to be
            // queued by the same handler, and the backlog belongs behind
            // those. It is judged now, held through the settle window, and
            // handed over by ReleaseHeld. Urgent skips all of it on purpose
            // (ledger and held set already cleared by DiscardAllLocked, but
            // the check keeps the policy explicit rather than an artifact of
            // call order).
            _readerBusyUntilUtc = now.AddMilliseconds(EstimateSpokenMs(message));

            if (intent == SpeechIntent.Urgent)
            {
                _believedQueued.Clear();
                return;
            }

            if (_believedQueued.Count == 0 && _held.Count == 0) return;

            // The interrupter may itself be the newer statement on a pending
            // entry's subject — a sweep over a field whose typed value is
            // still queued. Marked before the pass so the pass drops it and
            // the trace names the interrupter as what covered it. The mark
            // walks the held set too: a burst's second press can cover what
            // its first press rescued.
            if (!additive) MarkSupersededLocked(subject, $"'{message}'", origin, now);

            // A burst: whatever an earlier interrupt in this window already
            // holds is judged again — something may have covered it since —
            // and stays held. Its rescue count does not move, because nothing
            // was handed over and so nothing was rescued. That is what stops
            // two Tabs half a second apart spending both of an utterance's
            // rescues (213210 @4015266, @4015770, @4016234: capped on the
            // third press under the old contract).
            int carried = ReviewHeldLocked(now);

            var salvage = _believedQueued.ToArray();
            _believedQueued.Clear();
            int added = 0;
            foreach (var s in salvage)
            {
                string? refusal = SalvageRefusalLocked(s, now, atRescue: true);
                if (refusal != null)
                {
                    TraceDropLocked(s, refusal, now);
                    continue;
                }
                HoldLocked(s);
                added++;
            }

            if (_held.Count == 0)
            {
                // Everything was refused, each with its reason above. There
                // is nothing to wait for, and a hold that was open ends here.
                EndHoldLocked(null);
                return;
            }

            ArmHoldLocked(now, message, carried, added);
        }

        /// <summary>
        /// Put a rescued entry into the held set. The same cap as the ledger,
        /// for the same reason: purely defensive, and a train this deep is
        /// itself the bug the #197 transcript rule exists to catch. Overflow
        /// drops the OLDEST — the one most likely to have been heard — and
        /// says so.
        /// </summary>
        private void HoldLocked(BelievedQueued entry)
        {
            if (_held.Count >= LedgerCap)
            {
                var oldest = _held[0];
                _held.RemoveAt(0);
                Tracing.TraceLine(
                    $"SpeechArbiter: dropped a salvage (held set full at {LedgerCap}, oldest first) "
                    + $"after {oldest.SalvageCount} rescue(s): '{oldest.Message}'",
                    TraceLevel.Warning);
            }
            _held.Add(entry);
        }

        /// <summary>
        /// Judge every held entry again, now, and drop — with the reason —
        /// anything that no longer qualifies: a subject covered since it was
        /// held, or the ceiling crossed while it waited. Returns how many
        /// remain. The same function judges an entry at the interrupt and
        /// here, so a refusal reads the same in both places; the cap cannot
        /// change while held, and the word-count bound is deliberately not
        /// asked again — see <see cref="SalvageRefusalLocked"/>.
        /// </summary>
        private int ReviewHeldLocked(DateTime now)
        {
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                string? refusal = SalvageRefusalLocked(_held[i], now, atRescue: false);
                if (refusal == null) continue;
                TraceDropLocked(_held[i], refusal, now);
                _held.RemoveAt(i);
            }
            return _held.Count;
        }

        /// <summary>
        /// Open the settle window, or re-arm it for a further interrupt
        /// inside it. The hold's start, its interrupt count and the list of
        /// what went first survive a re-arm — they describe the hold as a
        /// whole, which is what the release trace reports.
        /// </summary>
        private void ArmHoldLocked(DateTime now, string behind, int carried, int added)
        {
            bool fresh = _holdTimer == null;
            if (fresh)
            {
                _holdStartedUtc = now;
                _holdInterrupts = 0;
                _wentFirst.Clear();
            }
            _holdInterrupts++;
            _holdBehind = behind;
            StartHoldTimerLocked();

            if (fresh)
            {
                Tracing.TraceLine(
                    $"SpeechArbiter: holding {_held.Count} salvage(s) for {SalvageSettleMs} ms behind "
                    + $"'{behind}' so its own follow-ups go first: {Quote(_held)}",
                    TraceLevel.Info);
            }
            else
            {
                Tracing.TraceLine(
                    $"SpeechArbiter: still holding {_held.Count} salvage(s) behind '{behind}' — "
                    + $"interrupt {_holdInterrupts} inside the window, {carried} carried and {added} added, "
                    + $"{(int)(now - _holdStartedUtc).TotalMilliseconds} ms since the hold began; "
                    + $"re-armed for {SalvageSettleMs} ms",
                    TraceLevel.Info);
            }
        }

        /// <summary>
        /// A fresh timer per arm rather than Change on the old one, so the
        /// callback captures the generation it belongs to. A callback already
        /// on its way to the lock when a later interrupt re-arms would
        /// otherwise release a window that had just been extended.
        /// </summary>
        private void StartHoldTimerLocked()
        {
            _holdTimer?.Dispose();
            int generation = ++_holdGeneration;
            _holdTimer = _clock.StartTimer(SalvageSettleMs, () => ReleaseHeld(generation));
        }

        /// <summary>
        /// The settle window closed: hand the train to the reader, in order,
        /// behind whatever the action queued meanwhile — or refuse each entry
        /// for a stated reason. Nothing leaves this method silently.
        /// </summary>
        private void ReleaseHeld(int generation)
        {
            lock (_lock)
            {
                // Overtaken: re-armed by a later interrupt, or cleared. The
                // window this callback was armed for is not the one closing.
                if (generation != _holdGeneration || _holdTimer == null) return;

                var now = _clock.UtcNow;
                int heldMs = (int)(now - _holdStartedUtc).TotalMilliseconds;

                if (_pending.Count > 0 && heldMs < SalvageCeilingMs)
                {
                    // A value is still sweeping, or a query is waiting out its
                    // gap. Its settle is an interrupt due within the anti-clip
                    // gap, and releasing now would put the train in front of
                    // it only to be flushed straight back into the ledger —
                    // a rescue spent on a few syllables, which is the clicks-
                    // and-ticks defect in a new place. Keep holding: the
                    // settle re-arms the window as any interrupt does, and
                    // the train lands once, after the sweep. Bounded by the
                    // ceiling so a pending entry that never flushes cannot
                    // hold the train forever.
                    Tracing.TraceLine(
                        $"SpeechArbiter: hold kept at {heldMs} ms, a value is still sweeping and its "
                        + $"settle would flush the train; re-armed for {SalvageSettleMs} ms",
                        TraceLevel.Info);
                    StartHoldTimerLocked();
                    return;
                }

                _holdTimer.Dispose();
                _holdTimer = null;

                var train = _held.ToArray();
                _held.Clear();
                int handed = 0;
                foreach (var s in train)
                {
                    string? refusal = SalvageRefusalLocked(s, now, atRescue: false);
                    if (refusal != null)
                    {
                        TraceDropLocked(s, refusal, now);
                        continue;
                    }

                    bool requeued = _sink(s.Message, false, s.Intent, s.Level, s.Origin, salvaged: true);
                    if (!requeued)
                    {
                        // Suppressed, or the backend went away while the
                        // train waited. Not re-entered, because it occupies
                        // nothing — but said, because silence here is the
                        // original sin.
                        Tracing.TraceLine(
                            $"SpeechArbiter: the reader did not take a salvage (suppressed or no backend) "
                            + $"after {s.SalvageCount} rescue(s): '{s.Message}'",
                            TraceLevel.Warning);
                        continue;
                    }

                    // Re-enter the ledger so a SECOND interrupt cannot destroy
                    // what the first one already had to salvage — bounded, now,
                    // by the count it carries with it.
                    s.SalvageCount++;
                    LedgerEnterLocked(s, now);
                    handed++;
                }

                Tracing.TraceLine(
                    $"SpeechArbiter: released {handed} of {train.Length} held salvage(s) {heldMs} ms after "
                    + $"'{_holdBehind}' ({_holdInterrupts} interrupt(s) in the window); "
                    + (_wentFirst.Count == 0
                        ? "nothing went first"
                        : $"{_wentFirst.Count} went first: {Quote(_wentFirst)}"),
                    TraceLevel.Info);

                _wentFirst.Clear();
                _holdInterrupts = 0;
                _holdBehind = string.Empty;
            }
        }

        /// <summary>
        /// Close the hold without handing anything over. With a reason, the
        /// held entries are let go and the trace names them and it; with
        /// none, the caller has already refused each entry with its own line
        /// and only the empty hold is being tidied away.
        /// </summary>
        private void EndHoldLocked(string? letGoReason)
        {
            bool wasHolding = _holdTimer != null;
            _holdTimer?.Dispose();
            _holdTimer = null;
            if (wasHolding) _holdGeneration++;

            if (_held.Count > 0)
            {
                Tracing.TraceLine(
                    $"SpeechArbiter: let go of {_held.Count} held salvage(s) unspoken, "
                    + $"{letGoReason ?? "no reason given"}: {Quote(_held)}",
                    TraceLevel.Info);
                _held.Clear();
            }
            else if (wasHolding)
            {
                Tracing.TraceLine(
                    $"SpeechArbiter: hold behind '{_holdBehind}' ended with nothing left to hand over; "
                    + "everything it held was refused, each with its reason above",
                    TraceLevel.Info);
            }

            _wentFirst.Clear();
            _holdInterrupts = 0;
            _holdBehind = string.Empty;
        }

        /// <summary>
        /// The drop line, in one place now that an entry can be refused at
        /// the interrupt, at a re-arm, or at the release. A salvage that
        /// gives up SILENTLY is the same defect class as the one every bound
        /// here exists to fix: speech that vanishes while the record says
        /// everything is fine. Say which bound was hit and what it was
        /// measured against — and, for a supersession, WHAT covered it. That
        /// line is the only reason #503 was ever found.
        /// </summary>
        private static void TraceDropLocked(BelievedQueued s, string refusal, DateTime now)
        {
            Tracing.TraceLine(
                $"SpeechArbiter: dropped a salvage ({refusal}) after "
                + $"{s.SalvageCount} rescue(s), "
                + $"{(int)(now - s.FirstEmittedUtc).TotalMilliseconds} ms after first "
                + $"emission: '{s.Message}'"
                + (s.Subject != null ? $" [subject '{s.Subject}']" : string.Empty),
                TraceLevel.Warning);
        }

        /// <summary>A short quoted list for the hold traces: the first three, each clipped, and a count of the rest.</summary>
        private static string Quote(IReadOnlyList<BelievedQueued> entries)
        {
            var names = new List<string>(entries.Count);
            foreach (var e in entries) names.Add(e.Message);
            return Quote(names);
        }

        private static string Quote(IReadOnlyList<string> messages)
        {
            const int show = 3, clip = 60;
            var parts = new List<string>(show);
            for (int i = 0; i < messages.Count && i < show; i++)
            {
                string m = messages[i];
                parts.Add("'" + (m.Length > clip ? m.Substring(0, clip) + "…" : m) + "'");
            }
            string joined = string.Join(", ", parts);
            return messages.Count > show ? $"{joined} and {messages.Count - show} more" : joined;
        }

        /// <summary>
        /// Why this utterance may NOT be salvaged again, or null when it may.
        /// The returned phrase goes straight into the trace, so it names the
        /// bound and the measurement rather than merely reporting a refusal.
        ///
        /// **The order is the policy (#503).** Supersession is asked first
        /// because it is the only refusal that says what made the words
        /// worthless; the cap and the ceiling are safety bounds and say so.
        /// The word-count bound comes last and only for an entry whose
        /// emitter declared no subject — for a keyed entry, age below the
        /// ceiling is not a reason. "SWR 1.7" at 3,863 ms is not stale; it is
        /// the answer to the last tune, and nothing has said otherwise.
        ///
        /// **The word-count bound is judged ONCE, at the interrupt that
        /// rescues the entry (#507).** Supersession and the ceiling are facts
        /// about now — something newer covers it, or it is fifteen seconds
        /// gone — and are asked again at every re-arm and at the release. The
        /// word-count bound is a heuristic about an entry the arbiter knows
        /// nothing about, and the settle window is the arbiter's OWN delay:
        /// charging the entry for it produced, in the first cut of this
        /// change, an unkeyed "SWR 1.7" that was fine at the interrupt and
        /// refused at the release for being fifteen milliseconds too old —
        /// a self-inflicted drop of exactly the class #503 exists to end.
        /// </summary>
        /// <param name="atRescue">
        /// True when judging at the interrupt that lifts the entry out of the
        /// ledger; false at a re-judge of the held set.
        /// </param>
        private string? SalvageRefusalLocked(BelievedQueued entry, DateTime now, bool atRescue)
        {
            int ageMs = (int)(now - entry.FirstEmittedUtc).TotalMilliseconds;

            if (entry.SupersededBy != null)
            {
                int laterMs = (int)(entry.SupersededAtUtc - entry.FirstEmittedUtc).TotalMilliseconds;
                return $"superseded {laterMs} ms after it by {entry.SupersededBy}"
                    + (string.IsNullOrEmpty(entry.SupersededByOrigin)
                        ? string.Empty
                        : $" from {entry.SupersededByOrigin}");
            }

            if (entry.SalvageCount >= MaxSalvages)
                return $"salvage cap: already rescued {entry.SalvageCount} times, limit {MaxSalvages}";

            if (ageMs > SalvageCeilingMs)
                return $"ceiling: {ageMs} ms old against the {SalvageCeilingMs} ms lifetime"
                    + (entry.Subject != null ? ", never superseded" : string.Empty);

            if (atRescue && entry.Subject == null)
            {
                int boundMs = EstimateSpokenMs(entry.Message) * SalvageAgeMultiple;
                if (ageMs > boundMs)
                    return $"stale: {ageMs} ms old against a {boundMs} ms bound; "
                        + "no subject declared, so only its word count could expire it";
            }

            return null;
        }

        /// <summary>
        /// Record that something newer covers <paramref name="subject"/>, on
        /// every pending entry that declared it. Idempotent per entry: the
        /// FIRST thing to cover it is what the trace names, because that is
        /// the moment the words stopped being worth hearing.
        /// </summary>
        private void MarkSupersededLocked(string? subject, string by, string? origin, DateTime now)
        {
            if (string.IsNullOrEmpty(subject)) return;
            MarkSupersededIn(_believedQueued, subject!, by, origin, now);
            // The held set is the same backlog one stage further from the
            // reader: "XIT +0" queued a millisecond after "RIT off" must
            // retire a held "XIT +100" exactly as it would a ledgered one.
            MarkSupersededIn(_held, subject!, by, origin, now);
        }

        private static void MarkSupersededIn(List<BelievedQueued> entries,
            string subject, string by, string? origin, DateTime now)
        {
            foreach (var e in entries)
            {
                if (e.SupersededBy != null) continue;
                if (!string.Equals(e.Subject, subject, StringComparison.Ordinal)) continue;
                e.SupersededBy = by;
                e.SupersededByOrigin = origin;
                e.SupersededAtUtc = now;
            }
        }

        /// <summary>A first entry into the ledger: this is emission number one.</summary>
        private void LedgerAddLocked(string message,
            SpeechIntent? intent, VerbosityLevel? level, string? origin, string? subject, DateTime now)
        {
            LedgerEnterLocked(new BelievedQueued
            {
                Message = message,
                Intent = intent,
                Level = level,
                Origin = origin,
                Subject = subject,
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
                        entry.Level, entry.Origin, entry.Subject);
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

        private void DiscardAllLocked(string reason)
        {
            foreach (var entry in _pending.Values) entry.Timer?.Dispose();
            _pending.Clear();

            // Forget what was last spoken as well, so the next value after
            // an urgent warning always speaks rather than being suppressed
            // as a duplicate of something the flush just discarded.
            _lastByKey.Clear();

            _believedQueued.Clear();
            _readerBusyUntilUtc = DateTime.MinValue;
            EndHoldLocked(reason);
        }
    }
}
