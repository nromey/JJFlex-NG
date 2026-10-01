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
    ///
    /// Returns a <see cref="SpeechHandoff"/> rather than the bool it once did
    /// (#521): the same "reached" fact, plus the ticket under which a
    /// completion channel will later report what the reader did with it. A
    /// sink with no such channel returns a bare bool, which converts to an
    /// untracked hand-off and behaves exactly as before.
    /// </summary>
    internal delegate SpeechHandoff SpeechSink(
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
    /// just shut up would defy them. (Both halves of that sentence are
    /// qualified as of 2026-09-23: neither may take a PROTECTED safety
    /// obligation with it. See the recovery-rule section below.)
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
    /// **And where the reader CAN tell the truth, the estimate is not used
    /// (#521).** The ledger modelled EMISSION, not delivery: five utterances
    /// handed to NVDA in one millisecond took twelve seconds to say, an
    /// interrupt ten seconds in made the whole backlog look unheard, and the
    /// rescue told an operator who had just disconnected that he was
    /// connected. Nothing here could learn otherwise. Now the sink's
    /// <see cref="SpeechHandoff"/> carries a ticket when a completion channel
    /// is carrying the utterance, and a tracked entry is never pruned by the
    /// clock: it leaves the ledger when <see cref="OnOutcome"/> says the
    /// reader finished it, stays with its last mark recorded when the reader
    /// says it was cut, and drops back to the estimate — traced as unknown —
    /// when nobody can say. The channel only informs; nothing here waits on
    /// it, so a reader without one gets exactly the arbiter it had. Where
    /// the backend offers is-speaking, a "not speaking" answer while the
    /// ledger believes the reader busy pulls the busy-until back to now — a
    /// correction to the estimate, never the basis of a queue.
    ///
    /// **And recovery attaches to the OBLIGATION, not to the next utterance
    /// (#606).** Everything above answers "what did the reader still have
    /// when something flushed it". None of it asked the prior question: is
    /// THIS interrupt a recovery opportunity for THIS unfinished unit at all?
    /// Until 2026-09-23 the answer was always yes — any successful ordinary
    /// interrupt swept the whole ledger — and an unrelated key acknowledgement
    /// was therefore permission to replay an old string. Measured in Noel's
    /// NVDA transcript of 2026-09-23 05:42: the listening sentence was
    /// submitted three times and cut at four, four and two words of ten,
    /// released 599 and 600 ms after the app acknowledged the JJ key and then
    /// Escape. Behind it sat a clause saying the station restore was
    /// unconfirmed; it was withdrawn before it ever started on every attempt,
    /// spent both its rescues at ADMISSION, and was never spoken once. **The
    /// lost information was the defect; the repetition was the symptom.**
    ///
    /// The rule, in Astra's words: recover an unfinished information
    /// obligation when it is still valid, belongs in the receiving context,
    /// and a known recovery opportunity permits it; never use an unrelated key
    /// acknowledgement as permission to replay an old string. Here that is
    /// <see cref="Owed"/> — what the transport actually established about the
    /// last attempt — consulted by <see cref="RecoveryPermitted"/>:
    ///
    /// - **Nothing said yet** — the reader or our pump still has it, and this
    ///   interrupt is about to destroy it. That IS the evidence, and it is the
    ///   whole pre-#521 contract. Recoverable.
    /// - **Cut by us** — we caused the loss, so we owe the put-back.
    ///   Recoverable.
    /// - **Never begun** — withdrawn at zero marks, or refused outright.
    ///   Nothing was heard, so handing it over again is a FIRST hearing, not a
    ///   repeat: recoverable, and it spends no rescue, because the cap bounds
    ///   repeats the operator may actually have heard.
    /// - **Paused, cause unknown** — cut part-way by something that is not us.
    ///   The completion callback cannot tell Ctrl from a focus change from
    ///   another program taking the foreground, so the cause stays unknown and
    ///   an unrelated later interrupt is NOT permission. It waits, owed,
    ///   until the ceiling retires it or its owner covers it.
    ///
    /// **Safety information is protected and is the exception to every bound
    /// here.** An interrupting warning used to be emitted and not ledgered at
    /// all, so an interrupted transmit-cut sentence was lost permanently
    /// rather than delayed. It is ledgered now, it survives the discard an
    /// urgent warning performs, it survives a Silence, and it is exempt from
    /// the salvage cap, the ceiling, the word-count bound and the ledger's
    /// overflow eviction — ordinary verbosity, navigation, a timer expiring
    /// and a rescue count running out may not retire it. What may: its owner
    /// saying something newer on the same subject. What still governs it is
    /// EVIDENCE: nothing is recovered without positive evidence the unit was
    /// lost, and an estimate saying the reader finished is evidence of
    /// delivery, not a bound on the obligation.
    ///
    /// Instance-based with an injected <see cref="ISpeechClock"/> so every
    /// timing constant here is testable exactly — advance a manual clock,
    /// assert precisely one utterance, assert when. Production wiring lives
    /// in <c>ScreenReaderOutput</c>, which remains the only public surface.
    /// </summary>
    internal sealed class SpeechArbiter
    {
        /// <summary>
        /// What the transport established about an entry's LAST delivery
        /// attempt — which is what decides whether a later interrupt may
        /// recover it. Deliberately not "how long ago was it": an elapsed
        /// estimate, a missing completion event and an unrelated keypress are
        /// none of them proof of loss (#606).
        /// </summary>
        internal enum Owed
        {
            /// <summary>Handed over; nobody has said anything about it yet. The reader or our pump still holds it.</summary>
            Pending,

            /// <summary>Our own interrupt, Silence or Urgent cut it. We caused the loss, so recovery is ours to offer.</summary>
            CutByUs,

            /// <summary>
            /// It never said a word: withdrawn at zero marks, or the reader
            /// refused the text. Fully owed — re-handing it is a first
            /// hearing, not a repeat.
            /// </summary>
            NeverStarted,

            /// <summary>
            /// Cut part-way by something that is not us. The cause is
            /// unclassified and must stay unclassified, so no unrelated
            /// interrupt may use it as permission to replay.
            /// </summary>
            PausedUnknownCause,
        }
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
        /// The OLD per-character rate, kept only as a rough yardstick for the
        /// connect briefing's settle trace and the queue-depth rule's
        /// comparison. **The ledger has not estimated by character since
        /// 2026-09-06 (#557).** Measured against NVDA speaking at the
        /// operator's own rate, this constant was about 60% high on every
        /// one of three utterances — 800 ms modelled against 551 actual, 6160
        /// against 2587, 13120 against 8079 — and high is the direction that
        /// hurts: it kept utterances in the ledger after the reader had
        /// finished them, so the salvage believed they went unheard. The
        /// estimate is now <see cref="SpeechRateModel"/>, an affine fit that
        /// prices words, pauses and expansions rather than characters, and
        /// learns the operator's actual rate from real deliveries.
        /// </summary>
        internal const int SalvageMsPerCharacter = 80;

        /// <summary>
        /// How far the ledger's estimate errs LONG over the rate model's best
        /// guess, in percent. The model answers "how long will this take";
        /// the ledger asks "how long must I protect this", and the costs of
        /// being wrong are not symmetric — see the class doc. Fifteen percent
        /// puts the three 2026-09-06 measurements 9 to 14 percent under the
        /// estimate, where the old constant had them 45 to 138 percent under.
        /// </summary>
        internal const int SalvageMarginPercent = 15;

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
        /// How many consecutive hand-overs of a PROTECTED obligation may come
        /// back having said nothing at all before automatic recovery backs off.
        ///
        /// A safety obligation is exempt from <see cref="MaxSalvages"/> and
        /// from <see cref="SalvageCeilingMs"/> on purpose — a rescue count
        /// running out may not retire it, and the hazard has not ceased just
        /// because a sentence was cut. But "never give up" and "chase every
        /// keypress forever" are different promises, and on a live desk every
        /// key the operator presses cancels the reader. So repeated
        /// cancellation WITHOUT PROGRESS pauses the automatic attempts — the
        /// obligation stays owed and is traced as undelivered rather than
        /// dropped. Any progress at all resets the count: a sentence getting
        /// further each time is not a loop.
        ///
        /// <para><b>What may un-pause it changed (#611).</b> It used to be any
        /// delivery completing anywhere, which is "an unrelated success
        /// happened" dressed as a recovery event. It is now a BACKEND-RECOVERY
        /// EDGE — the transition out of a channel known to be refusing or
        /// absent — taken exactly once per transition, and never for an
        /// obligation the operator silenced.</para>
        /// </summary>
        internal const int ProtectedAttemptsWithoutProgress = 3;

        /// <summary>
        /// How many automatic hand-overs one protected obligation may have
        /// before automatic speech for it pauses — counting every attempt,
        /// including ones nobody ever reported on.
        ///
        /// <para><see cref="ProtectedAttemptsWithoutProgress"/> counts what the
        /// transport SAID, so on an untracked backend it stays at zero forever
        /// and bounds nothing: such an obligation would chase every interrupt
        /// for as long as the application ran. This budget is the other half —
        /// one initial attempt and one automatic retry, after which the fact is
        /// still owed, still in the ledger and still reachable, and automatic
        /// work for it stops until a recovery edge or an explicit replay.</para>
        ///
        /// <para>Pausing is not dropping, and the distinction is the whole
        /// point: "we have stopped trying for now" and "nothing is owed" are
        /// different states.</para>
        /// </summary>
        internal const int ProtectedAutomaticAttempts = 2;

        /// <summary>
        /// How many protected obligations may be registered at once.
        ///
        /// <para>Protected entries were exempt from the cap, the ceiling, the
        /// word-count bound and overflow eviction — correctly, each one for its
        /// own reason — which left them exempt from EVERYTHING, so repeated
        /// unkeyed warnings could accumulate without limit and take release
        /// slots from one another. A fixed condition has a finite catalogue and
        /// a slot each, so the realistic occupancy is small; this ceiling
        /// exists for the case where it is not.</para>
        ///
        /// <para><b>Overflow REFUSES the new registration, loudly, and never
        /// evicts an unheard one.</b> Silently discarding a warning while
        /// continuing to behave as though it were being monitored is the
        /// failure this whole class exists to end. The warning is still spoken;
        /// what it does not get is a retained obligation, and the trace says
        /// exactly that.</para>
        /// </summary>
        internal const int ProtectedSlotCap = 8;

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

            /// <summary>
            /// When the reader is estimated to have finished saying it.
            /// <see cref="DateTime.MaxValue"/> for a TRACKED entry: the
            /// clock never retires one of those, only the reader's own
            /// answer does (#521).
            /// </summary>
            public DateTime EstFinishUtc;

            /// <summary>
            /// The completion channel's ticket, or 0 when nobody will report
            /// on this entry and the estimate governs it. Renewed on every
            /// hand-over, because each hand-over is a new question to the
            /// reader.
            /// </summary>
            public long Ticket;

            /// <summary>
            /// For an entry the reader reported CUT: how many words it got
            /// through, of how many. Recorded so the salvage trace can say
            /// "cut at word 3 of 12" rather than guess, and so a future rule
            /// could resume from there. Zero until an outcome says otherwise.
            /// </summary>
            public int MarksReached;
            public int MarkCount;

            /// <summary>
            /// The furthest any attempt on this obligation ever got. Kept
            /// because <see cref="ReleaseHeld"/> used to zero the marks on
            /// every re-hand, which threw away the one fact that says whether
            /// the operator heard any of it — and therefore whether handing it
            /// over again is a repeat or a first hearing (#606).
            /// </summary>
            public int MarksReachedEver;

            /// <summary>The last thing the channel said about this entry, for the trace; null when it has said nothing yet.</summary>
            public SpeechOutcomeKind? LastOutcome;

            /// <summary>
            /// What the transport established about the last attempt — see
            /// <see cref="Owed"/>. This, not the clock and not the arrival of
            /// an unrelated interrupt, is what decides recovery.
            /// </summary>
            public Owed State = Owed.Pending;

            /// <summary>
            /// A safety obligation (<see cref="SpeechIntent.Urgent"/>). Exempt
            /// from the cap, the ceiling, the word-count bound and the
            /// overflow eviction; survives the discard an urgent warning
            /// performs and survives a Silence. Only its owner's own newer
            /// statement on the same subject retires it.
            /// </summary>
            public bool Protected;

            /// <summary>
            /// Consecutive attempts that came back having said nothing. Drives
            /// <see cref="ProtectedAttemptsWithoutProgress"/>; reset by any
            /// progress at all.
            /// </summary>
            public int AttemptsWithoutProgress;

            /// <summary>
            /// Set when a protected obligation's automatic attempts have backed
            /// off. It is still owed and still in the ledger; it simply stops
            /// chasing every interrupt until a genuine recovery edge.
            /// </summary>
            public bool AutoRecoveryPaused;

            /// <summary>
            /// Every automatic hand-over of this obligation, whether or not
            /// anything ever reported on it. The sibling
            /// <see cref="AttemptsWithoutProgress"/> counts what the TRANSPORT
            /// said, and an untracked backend never says anything at all — so
            /// on that path it stays at zero forever and bounds nothing. This
            /// one bounds the attempts themselves
            /// (<see cref="ProtectedAutomaticAttempts"/>), which is the budget
            /// Astra's contract asks for: two counters because there are two
            /// different ways for an attempt to be wasted.
            /// </summary>
            public int AutomaticAttempts;

            /// <summary>
            /// Extra attempts granted by a backend-recovery edge, one per
            /// edge. Added to <see cref="ProtectedAutomaticAttempts"/> rather
            /// than resetting <see cref="AutomaticAttempts"/>, because a
            /// recovery edge grants one probe and does not clear what the
            /// episode has already spent — otherwise a flapping channel
            /// manufactures fresh budgets forever.
            /// </summary>
            public int ProbesGranted;

            /// <summary>The automatic attempts this obligation has left, counting any probe a recovery edge granted.</summary>
            public bool AutomaticBudgetLeft => AutomaticAttempts < ProtectedAutomaticAttempts + ProbesGranted;

            /// <summary>
            /// True once an attempt's occupancy estimate ran out with nobody
            /// able to report on it, or the maximum turn was reached. The
            /// obligation is NOT discharged by either — "we cannot tell
            /// whether this was heard" is recorded as itself.
            /// </summary>
            public bool DeliveryUnknown;

            /// <summary>
            /// The quiet cohort this obligation was admitted under. An
            /// obligation from an older cohort was stopped by the operator and
            /// is not resurrected by a passing interrupt (#182); it waits for
            /// an explicit replay or a known owner recovery event.
            /// </summary>
            public long QuietGeneration;

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

        /// <summary>
        /// The one scheduler for the safety speaking turn (#611). Transmit
        /// safety, the transmit-safety retry and every operator alarm take
        /// their turn from here, so a turn can be reasoned about rather than
        /// inferred from a deadline and a flag. Touched only under
        /// <see cref="_lock"/>, which is also what serialises the sink call
        /// against the outcome callback — that is what makes
        /// reserve-hand-over-bind atomic.
        /// </summary>
        private readonly SafetyDeliveryCoordinator _safety;

        /// <summary>The quiet cohort the arbiter is currently serving. Read by the alarm cue stage, which arms timers before the arbiter ever sees the warning.</summary>
        internal long SafetyQuietGeneration { get { lock (_lock) return _safety.QuietGeneration; } }

        /// <summary>
        /// What one safety obligation is, seen from outside the ledger: the
        /// fact, whose it is, and the three separate things that are NOT the
        /// same question — has it been delivered, is automatic speech still
        /// trying, and did the operator stop it.
        /// </summary>
        internal readonly record struct OwedSafety(
            string Message,
            string? Subject,
            bool DeliveryUnknown,
            bool AutomaticSpeechPaused,
            bool SilencedByOperator,
            int Attempts);

        /// <summary>
        /// The safety obligations still owed, in the order they were admitted.
        ///
        /// <para><b>Why this exists, and why it is not a product surface.</b>
        /// "Retained but paused" and "silently dropped" are indistinguishable
        /// from the outside — both are silence — so a test asserting that a
        /// silenced warning survives has nothing to look at and an absence
        /// proves the wrong thing. This is the honest observation that makes
        /// the claim checkable.</para>
        ///
        /// <para>It is also the shape the operator's reachable pending list
        /// will read when somebody builds it, which is the biggest thing this
        /// layer still owes: today an undelivered safety obligation is
        /// recorded here and traced at Error, and <b>a trace is not operator
        /// awareness</b>.</para>
        /// </summary>
        internal IReadOnlyList<OwedSafety> OwedSafetyObligations
        {
            get
            {
                lock (_lock)
                {
                    var owed = new List<OwedSafety>();
                    foreach (var e in _believedQueued)
                    {
                        if (!e.Protected) continue;
                        owed.Add(new OwedSafety(e.Message, e.Subject, e.DeliveryUnknown,
                            e.AutoRecoveryPaused || !e.AutomaticBudgetLeft,
                            !_safety.InCurrentCohort(e.QuietGeneration), e.AutomaticAttempts));
                    }
                    foreach (var e in _held)
                    {
                        if (!e.Protected) continue;
                        owed.Add(new OwedSafety(e.Message, e.Subject, e.DeliveryUnknown,
                            e.AutoRecoveryPaused || !e.AutomaticBudgetLeft,
                            !_safety.InCurrentCohort(e.QuietGeneration), e.AutomaticAttempts));
                    }
                    return owed;
                }
            }
        }

        private readonly ISpeechClock _clock;
        private readonly Func<VerbosityLevel> _verbosity;
        private readonly SpeechSink _sink;
        private readonly Action _silenceBackend;
        private readonly Action<string, VerbosityLevel, SpeechIntent?, string?> _recordGated;
        private readonly SpeechRateModel _rate;
        private readonly Func<bool?>? _isSpeaking;
        private readonly Func<long, string, bool>? _withdrawUnsent;

        /// <param name="clock">Time source. Inject a manual clock to test.</param>
        /// <param name="verbosity">Read at flush time — the setting can move while a value is pending.</param>
        /// <param name="sink">Where decided utterances go. See <see cref="SpeechSink"/>.</param>
        /// <param name="silenceBackend">Cut current speech now. Used by Urgent only.</param>
        /// <param name="recordGated">Transcript record for "fired but the verbosity filter dropped it".</param>
        /// <param name="rate">
        /// The speaking-rate model the estimate path uses (#557). Null means
        /// a fresh, uncalibrated model at the reference rate — what every
        /// test wants and what production supplies a persisted one for.
        /// </param>
        /// <param name="isSpeaking">
        /// The backend's is-speaking bit, or null when it cannot report one.
        /// Returns null when unavailable at call time. Consulted only as a
        /// CORRECTION to the estimate — a "no" while the ledger believes the
        /// reader busy clamps the busy-until — never as the basis of a queue.
        /// </param>
        /// <param name="withdrawUnsent">
        /// Take an utterance back out of the delivery pump by ticket, if it is
        /// still unsent, and say true when it was. Null where there is no pump.
        ///
        /// <para><b>Why supersession needs this (#606).</b> Marking an entry
        /// superseded stops it being RESCUED and does nothing about a copy the
        /// pump has already been handed and has not yet started — so a progress
        /// heartbeat and the line that answers it could still be spoken back to
        /// back, and a dialog's title could still arrive after the dialog had
        /// gone. The old source comment said submitted text cannot be taken
        /// back; that stopped being true when #521 put our own queue in front
        /// of the reader's, and this is the consequence nobody collected.</para>
        /// </param>
        public SpeechArbiter(
            ISpeechClock clock,
            Func<VerbosityLevel> verbosity,
            SpeechSink sink,
            Action silenceBackend,
            Action<string, VerbosityLevel, SpeechIntent?, string?> recordGated,
            SpeechRateModel? rate = null,
            Func<bool?>? isSpeaking = null,
            Func<long, string, bool>? withdrawUnsent = null)
        {
            _clock = clock;
            _verbosity = verbosity;
            _sink = sink;
            _silenceBackend = silenceBackend;
            _recordGated = recordGated;
            _rate = rate ?? new SpeechRateModel();
            _isSpeaking = isSpeaking;
            _withdrawUnsent = withdrawUnsent;

            // The maximum turn's cancellation request is the same cut an
            // urgent warning makes: silence the backend. The coordinator asks
            // for it under this lock and the pump answers by ticket, which is
            // what turns the limit into a handshake (#611).
            _safety = new SafetyDeliveryCoordinator(() => { try { _silenceBackend(); } catch { } });
        }

        /// <summary>
        /// Estimated milliseconds the reader spends saying <paramref name="message"/>,
        /// for ledger protection, at the REFERENCE rate: the
        /// <see cref="SpeechRateModel"/> fit plus <see cref="SalvageMarginPercent"/>,
        /// floored and capped. The instance path (<see cref="EstimateLocked"/>)
        /// applies the same shape to the calibrated model; with no
        /// calibration the two agree exactly, which is what lets a test quote
        /// this one. Deliberately NOT shared with the #197 transcript
        /// queue-depth rule, whose estimate must err realistic where this
        /// one must err generous — see SpeechQueueDepthRule's class doc for
        /// the asymmetry argument.
        /// </summary>
        internal static int EstimateSpokenMs(string message) =>
            Bound(SpeechRateModel.Uncalibrated(message));

        /// <summary>The estimate at the learned rate. Callers hold the lock only by convention; the model is thread-safe itself.</summary>
        private int EstimateLocked(string message) => Bound(_rate.Estimate(message));

        private static int Bound(int modelledMs) =>
            Math.Min(SalvageCapMs, Math.Max(SalvageMinMs, modelledMs * (100 + SalvageMarginPercent) / 100));

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
        ///
        /// <para><b>What changed, and it is the half that was missing (#606,
        /// #571's third HIGH finding).</b> The discard used to take EVERYTHING,
        /// including any earlier safety obligation still owed, and the warning
        /// itself was then emitted and never entered in the ledger — so an
        /// interrupted transmit-cut sentence was lost permanently rather than
        /// delayed, and the one sentence that must survive was the one that
        /// did not. Now the discard clears the ORDINARY backlog and spares
        /// protected obligations, and the warning enters the ledger as one —
        /// BEFORE the handoff, so a reader that refuses it leaves a fact
        /// behind rather than nothing at all.</para>
        ///
        /// <para><paramref name="subject"/> is carried through rather than
        /// dropped. It is what lets the safety owner's next episode retire an
        /// unheard earlier one — the only lifecycle event that may — and the
        /// callers have been passing it all along into a parameter that threw
        /// it away.</para>
        ///
        /// <para><b>And it takes the safety speaking turn (#611).</b> Priority
        /// is an explicit class held by <see cref="SafetyDeliveryCoordinator"/>,
        /// never the absence of a subject string. An operator alarm that is
        /// sounding is pre-empted here and an alarm waiting its turn waits
        /// behind this one; the pre-empted alarm's own record stays with the
        /// alarm subsystem, which owns it.</para>
        /// </summary>
        public void Urgent(string message, VerbosityLevel level, string? origin, string? subject = null)
        {
            lock (_lock)
            {
                var now = _clock.UtcNow;
                SafetyTurn turn = _safety.ReserveForSafety(subject, now);

                DiscardOrdinaryLocked("an urgent warning discards everything queued");
                try { _silenceBackend(); } catch { }
                EmitUrgentLocked(message, level, origin, subject, additive: false, now, turn);

                // Alarms that were waiting behind whatever held the turn now
                // wait behind this one. Re-armed rather than left pointing at
                // a deadline that no longer exists.
                if (_alarmPending.Count > 0) ArmAlarmTimerLocked(now);
            }
        }

        // ── Operator alarms: the alarm-aware urgent (#566, design section 5) ──
        //
        // Urgent alone gets an alarm past stale speech; it cannot promise the
        // alarm's own sentence will finish, and two alarms using it would cut
        // each other off forever. So an alarm carries a SUBJECT (its identity)
        // and a REFRESH (how to re-read the condition), and it takes its turn
        // from the same coordinator transmit safety takes its turn from: a
        // transmit-safety announcement always wins, and an alarm that arrives
        // while any safety attempt holds the turn waits, re-reads itself when
        // its turn comes, and speaks only if it is still true.
        //
        // **What changed at the merge (#611).** Track I represented the turn
        // as a pair of fields — a deadline and a flag — and remembered the
        // last alarm's ticket beside them. An alarm's LATE completion, arriving
        // after a transmit cut had taken over, matched that remembered ticket,
        // collapsed the deadline and released a queued alarm; that alarm's
        // handoff then called the FULL teardown, which really does clear
        // protected entries, and the cut sentence was gone. Both halves are
        // closed here: the turn is a token that only its own ticket can
        // release, and the alarm handoff takes the ordinary discard.
        //
        // Neither the alarm's condition evaluation nor its acknowledgement,
        // snooze or reminder semantics moved. Those belong to the alarm
        // subsystem and stay there; this is a speech-boundary integration.

        /// <summary>How many alarm sentences may wait behind a sounding safety attempt. Overflow drops the OLDEST and says so.</summary>
        internal const int AlarmPendingCap = 8;

        /// <summary>Waiting longer than this is recorded as a delivery-late condition — never a reason to pre-empt the cut.</summary>
        internal const int AlarmDeliveryLateMs = 5000;

        /// <summary>How long after a cancellation that said nothing to let the reader settle before the one retry.</summary>
        internal const int AlarmRetrySettleMs = 600;

        /// <summary>The retry happens inside this window from the original hand-over, or not at all.</summary>
        internal const int AlarmRetryWindowMs = 5000;

        private sealed class PendingAlarm
        {
            public string Subject = string.Empty;
            public string Message = string.Empty;
            public VerbosityLevel Level;
            public string? Origin;
            public Func<string?> Refresh = () => null;
            public DateTime QueuedUtc;
        }

        private readonly List<PendingAlarm> _alarmPending = new List<PendingAlarm>();

        private ISpeechTimer? _alarmTimer;
        private int _alarmGeneration;

        // The last alarm handed over, for the one bounded retry. Keyed by the
        // TURN it was handed over on, not by a bare ticket: a remembered
        // ticket outlives the turn it belonged to, and that is exactly how a
        // dead alarm released a live cut.
        private long _lastAlarmTurnId;
        private string _lastAlarmSubject = string.Empty;
        private Func<string?>? _lastAlarmRefresh;
        private VerbosityLevel _lastAlarmLevel;
        private string? _lastAlarmOrigin;
        private DateTime _lastAlarmEmittedUtc;
        private bool _lastAlarmRetried;
        private ISpeechTimer? _retryTimer;

        /// <summary>Alarm sentences waiting behind a sounding safety attempt. Tests.</summary>
        internal int AlarmPendingCount { get { lock (_lock) return _alarmPending.Count; } }

        /// <summary>
        /// Speak an operator alarm's warning as an urgent, under the priority
        /// contract above.
        /// </summary>
        /// <param name="subject">The alarm's identity (<see cref="SpeechSubject.OperatorAlarm"/>). One pending sentence per subject.</param>
        /// <param name="refresh">
        /// Re-read the condition and return the sentence to say NOW, or null
        /// when there is nothing left to say — cleared, acknowledged, data
        /// gone. Called under the arbiter's lock; must be cheap and must not
        /// speak.
        /// </param>
        public void UrgentAlarm(string message, VerbosityLevel level, string? origin,
            string subject, Func<string?> refresh)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (_lock)
            {
                var now = _clock.UtcNow;
                SafetyTurn? turn = _safety.TryReserveForAlarm(subject, now);
                if (turn == null)
                {
                    DeferAlarmLocked(message, level, origin, subject, refresh, now);
                    return;
                }
                EmitAlarmLocked(turn, message, level, origin, subject, refresh, now, deferredMs: 0, why: null);
            }
        }

        /// <summary>Something holds the turn: wait, keeping one pending sentence per alarm.</summary>
        private void DeferAlarmLocked(string message, VerbosityLevel level, string? origin,
            string subject, Func<string?> refresh, DateTime now)
        {
            DateTime? ends = _safety.EndsAtUtc;
            int waitMs = ends == null ? 0 : Math.Max(0, (int)(ends.Value - now).TotalMilliseconds);

            PendingAlarm? existing = _alarmPending.Find(p => string.Equals(p.Subject, subject, StringComparison.Ordinal));
            if (existing != null)
            {
                // A newer statement on the same subject replaces the waiting
                // one; the queue position is kept, so one noisy producer
                // cannot reset every other producer's wait.
                existing.Message = message;
                existing.Level = level;
                existing.Origin = origin;
                existing.Refresh = refresh;
            }
            else
            {
                if (_alarmPending.Count >= AlarmPendingCap)
                {
                    PendingAlarm oldest = _alarmPending[0];
                    _alarmPending.RemoveAt(0);
                    Tracing.TraceLine(
                        $"SpeechArbiter: alarm pending set full at {AlarmPendingCap}; dropped the oldest, "
                        + $"'{oldest.Message}' [subject '{oldest.Subject}']. Its condition is unchanged and is "
                        + "still in the alarms list.", TraceLevel.Warning);
                }
                _alarmPending.Add(new PendingAlarm
                {
                    Subject = subject, Message = message, Level = level, Origin = origin,
                    Refresh = refresh, QueuedUtc = now,
                });
            }

            Tracing.TraceLine(
                $"SpeechArbiter: alarm deferred about {waitMs} ms behind "
                + (_safety.SafetyHoldsTheTurn(now) ? "a transmit-safety announcement" : "another alarm")
                + $": '{message}' [subject '{subject}']", TraceLevel.Info);
            ArmAlarmTimerLocked(now);
        }

        /// <summary>
        /// The hand-over itself: cancel the ORDINARY transport, cut the
        /// reader, emit, and bind the turn to the ticket that came back.
        ///
        /// <para><b>The discard here is the ordinary one, and that is the
        /// #611 fix.</b> Track I called <c>DiscardAllLocked</c>, which is
        /// intentionally the full teardown and really does clear protected
        /// entries and the held set. Track J changed the safety caller of that
        /// method, not its meaning, and Track I supplied another caller — so a
        /// normally admitted alarm could erase an unfinished transmit-cut
        /// fact. An alarm needs the channel clear of runnable ordinary text;
        /// it has never needed to delete a safety obligation.</para>
        /// </summary>
        private void EmitAlarmLocked(SafetyTurn turn, string message, VerbosityLevel level, string? origin,
            string subject, Func<string?> refresh, DateTime now, int deferredMs, string? why)
        {
            DiscardOrdinaryLocked("an alarm warning discards everything queued");
            try { _silenceBackend(); } catch { }
            var handoff = _sink(message, true, SpeechIntent.Urgent, level, origin, salvaged: false);
            if (!handoff.Reached)
            {
                _safety.Abandon(turn, "the reader did not take an alarm warning (suppressed or no backend)");
                Tracing.TraceLine(
                    $"SpeechArbiter: the reader did not take an alarm warning (suppressed or no backend): '{message}'. "
                    + "The condition is unchanged and remains in the alarms list.",
                    TraceLevel.Warning);
                return;
            }

            int estimate = EstimateLocked(message);
            _safety.Bind(turn, handoff, estimate, now);
            if (!handoff.Tracked) _readerBusyUntilUtc = now.AddMilliseconds(estimate);

            _lastAlarmTurnId = turn.Id;
            _lastAlarmSubject = subject;
            _lastAlarmRefresh = refresh;
            _lastAlarmLevel = level;
            _lastAlarmOrigin = origin;
            _lastAlarmEmittedUtc = now;
            _lastAlarmRetried = why != null && why.StartsWith("retry", StringComparison.Ordinal);

            if (deferredMs > 0)
            {
                Tracing.TraceLine(
                    $"SpeechArbiter: alarm spoken after a {deferredMs} ms deferral"
                    + (deferredMs > AlarmDeliveryLateMs ? " — DELIVERY LATE, over the five-second bound" : string.Empty)
                    + $": '{message}' [subject '{subject}']",
                    deferredMs > AlarmDeliveryLateMs ? TraceLevel.Warning : TraceLevel.Info);
            }
            else if (why != null)
            {
                Tracing.TraceLine($"SpeechArbiter: alarm {why}: '{message}' [subject '{subject}']", TraceLevel.Info);
            }
        }

        private void ArmAlarmTimerLocked(DateTime now)
        {
            DateTime? ends = _safety.EndsAtUtc;
            int due = ends == null ? 50 : Math.Max(50, (int)(ends.Value - now).TotalMilliseconds + 50);
            _alarmTimer?.Dispose();
            int generation = ++_alarmGeneration;
            _alarmTimer = _clock.StartTimer(due, () => ReleaseAlarms(generation));
        }

        /// <summary>
        /// The safety turn is believed free: give the next waiting alarm its
        /// turn — after asking it whether it is still true. One at a time,
        /// each re-arming for the next, so two alarms follow one another
        /// rather than cancelling one another.
        /// </summary>
        private void ReleaseAlarms(int generation)
        {
            lock (_lock)
            {
                if (generation != _alarmGeneration) return;
                _alarmTimer = null;
                var now = _clock.UtcNow;

                while (_alarmPending.Count > 0)
                {
                    if (_safety.IsBusy(now))
                    {
                        // Still occupied — a cut that is running long, or a
                        // newer safety announcement that took the turn while
                        // this timer was pending. Wait again rather than
                        // speaking over it.
                        ArmAlarmTimerLocked(now);
                        return;
                    }

                    PendingAlarm next = _alarmPending[0];
                    _alarmPending.RemoveAt(0);
                    string? current = null;
                    try { current = next.Refresh(); } catch (Exception ex)
                    { Tracing.TraceLine($"SpeechArbiter: an alarm's refresh threw — {ex.Message}", TraceLevel.Warning); }

                    if (current == null)
                    {
                        Tracing.TraceLine(
                            $"SpeechArbiter: a waiting alarm was dropped because it is no longer current: "
                            + $"'{next.Message}' [subject '{next.Subject}']", TraceLevel.Info);
                        continue;
                    }

                    SafetyTurn? turn = _safety.TryReserveForAlarm(next.Subject, now);
                    if (turn == null)
                    {
                        _alarmPending.Insert(0, next);
                        ArmAlarmTimerLocked(now);
                        return;
                    }

                    int deferredMs = (int)(now - next.QueuedUtc).TotalMilliseconds;
                    EmitAlarmLocked(turn, current, next.Level, next.Origin, next.Subject, next.Refresh,
                        now, deferredMs, why: null);
                    break;
                }

                if (_alarmPending.Count > 0) ArmAlarmTimerLocked(now);
            }
        }

        /// <summary>
        /// The reader's answer about a safety hand-over — transmit safety or
        /// alarm — routed through the one occupancy token (#611).
        ///
        /// <para><b>An answer releases the turn only when it is the CURRENT
        /// turn's own ticket.</b> A late Completed and a late Cancelled from
        /// an attempt something else has since pre-empted both fall here,
        /// update that attempt's own evidence in the ledger through the
        /// ordinary path below, and free nothing. That is the whole of the
        /// permitted alarm / cut / alarm interleaving, closed.</para>
        /// </summary>
        private void OnSafetyOutcomeLocked(long ticket, SpeechOutcome outcome, DateTime now)
        {
            bool completed = outcome.Kind == SpeechOutcomeKind.Completed;
            SafetyTurn? ended = _safety.TakeOutcome(ticket, completed, now);
            if (ended == null) return;

            if (ended.Class == SafetyClass.OperatorAlarm && ended.Id == _lastAlarmTurnId)
                ConsiderAlarmRetryLocked(ended, outcome, now);

            if (_alarmPending.Count > 0) ArmAlarmTimerLocked(now);
        }

        /// <summary>
        /// The one bounded retry, and what it is now allowed to assume.
        ///
        /// <para>Track I retried on any cancellation not by us, and its trace
        /// named the cause: "a focus change or the operator's key". <b>The
        /// callback cannot know that</b> — Ctrl, a focus change and another
        /// program taking the foreground are indistinguishable there, which is
        /// the same claim Track J had to take out of the withdrawal trace one
        /// file over. The retry now goes through the QUIET CONTRACT instead of
        /// through a guess: if the operator really did silence speech, the
        /// cohort advanced and this warning is not retried at all. If the
        /// cohort is unchanged, nothing here claims to know what happened, and
        /// the trace says so.</para>
        ///
        /// <para><b>What this deliberately does NOT do.</b> Astra's design
        /// goes further and would pause the retry after ANY unidentifiable
        /// cancellation, "including zero-word withdrawals", until an explicit
        /// replay. That rule needs a reachable pending surface to replay FROM,
        /// and there is none yet — so adopting it here would turn one stray
        /// keypress into a warning the operator cannot get back. The brief
        /// scopes the pause to deliberate silence, which is what is built.
        /// The difference is reported rather than decided here.</para>
        /// </summary>
        private void ConsiderAlarmRetryLocked(SafetyTurn ended, SpeechOutcome outcome, DateTime now)
        {
            if (outcome.Kind != SpeechOutcomeKind.Cancelled) return;
            if (outcome.CancelledByUs || _lastAlarmRetried || _lastAlarmRefresh == null) return;
            if (!_safety.InCurrentCohort(ended.QuietGeneration))
            {
                Tracing.TraceLine(
                    $"SpeechArbiter: no alarm retry — the operator silenced this cohort [subject '{_lastAlarmSubject}']. "
                    + "The condition is unchanged and is still in the alarms list.", TraceLevel.Info);
                return;
            }
            if ((now - _lastAlarmEmittedUtc).TotalMilliseconds > AlarmRetryWindowMs) return;

            _lastAlarmRetried = true;
            Tracing.TraceLine(
                $"SpeechArbiter: an alarm was cancelled at word {outcome.MarksReached} of {outcome.MarkCount} by a "
                + "cause this callback cannot identify — the operator's key, a focus change and another program "
                + "taking the foreground all arrive here alike, and the operator has not silenced speech. One retry "
                + $"in {AlarmRetrySettleMs} ms if it is still true [subject '{_lastAlarmSubject}']", TraceLevel.Info);
            _retryTimer?.Dispose();
            string subject = _lastAlarmSubject;
            Func<string?> refresh = _lastAlarmRefresh;
            VerbosityLevel level = _lastAlarmLevel;
            string? origin = _lastAlarmOrigin;
            DateTime emitted = _lastAlarmEmittedUtc;
            long cohort = ended.QuietGeneration;
            _retryTimer = _clock.StartTimer(AlarmRetrySettleMs,
                () => RetryAlarm(subject, refresh, level, origin, emitted, cohort));
        }

        private void RetryAlarm(string subject, Func<string?> refresh, VerbosityLevel level, string? origin,
            DateTime emitted, long cohort)
        {
            lock (_lock)
            {
                _retryTimer = null;
                var now = _clock.UtcNow;
                if (!string.Equals(subject, _lastAlarmSubject, StringComparison.Ordinal)) return;   // something newer took over
                if (!_safety.InCurrentCohort(cohort)) return;                                       // silenced while it settled
                if ((now - emitted).TotalMilliseconds > AlarmRetryWindowMs) return;
                SafetyTurn? turn = _safety.TryReserveForAlarm(subject, now);
                if (turn == null) return;   // a safety announcement got in first; it wins
                string? current = null;
                try { current = refresh(); } catch { }
                if (current == null)
                {
                    _safety.ReleaseUnused(turn, "the alarm is no longer current, so the retry was not made");
                    Tracing.TraceLine($"SpeechArbiter: alarm retry not made, no longer current [subject '{subject}']", TraceLevel.Info);
                    return;
                }
                EmitAlarmLocked(turn, current, level, origin, subject, refresh, now, deferredMs: 0,
                    why: "retry once, because the first hand-over said nothing at all");
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
        /// <para><b>A protected obligation is not forgotten here (#606).</b>
        /// Silence stops the sound; it is not the operator saying they
        /// understood a transmit-safety condition, and the hazard has not
        /// ceased because a key was pressed. So a safety obligation still owed
        /// is kept, paused, and the ordinary backlog around it goes.</para>
        ///
        /// <para><b>And "paused" now means something enforceable (#182).</b>
        /// Keeping the obligation was only half of it: the next unrelated
        /// interrupt treated every protected entry as recoverable, including
        /// one the operator had just stopped, and requeued it 600 ms later.
        /// So the QUIET COHORT advances here. Every safety episode admitted
        /// before this instant — the sounding attempt, the ledger entries, the
        /// held train, the alarms waiting their turn and the alarm retry —
        /// belongs to the old cohort, and automatic speech does not serve it
        /// again. Nothing is acknowledged, nothing is erased and no condition
        /// is disabled; an explicit replay, or a positively correlated owner
        /// recovery event, is what makes it eligible again.</para>
        public void OnSilenced()
        {
            lock (_lock)
            {
                var now = _clock.UtcNow;

                // First, so that everything below is judged against the NEW
                // cohort. A callback still in flight for an attempt admitted
                // before this line inherits the pause when it lands, because
                // the cohort is identified by when the episode was ADMITTED,
                // not by when its answer arrives.
                _safety.Silence(now);

                KeepProtectedLocked("the operator silenced speech");
                _readerBusyUntilUtc = DateTime.MinValue;
                EndHoldLocked("the operator silenced speech", keepProtected: true);

                // Alarms waiting their turn, and the one bounded retry, go
                // too: the operator asked for quiet, and an alarm that
                // matters is still in the alarms list and in Read active
                // alarms, which is where its record lives.
                if (_alarmPending.Count > 0)
                    Tracing.TraceLine($"SpeechArbiter: {_alarmPending.Count} waiting alarm(s) let go, the operator silenced speech; "
                        + "every one of their conditions is unchanged and still in the alarms list",
                        TraceLevel.Info);
                _alarmPending.Clear();
                _alarmTimer?.Dispose(); _alarmTimer = null; _alarmGeneration++;
                _retryTimer?.Dispose(); _retryTimer = null;
                _lastAlarmRefresh = null;
            }
        }

        /// <summary>
        /// Drop all pending state, protected obligations included. Shutdown
        /// and the test reset, and nothing else — an urgent warning takes
        /// <see cref="DiscardOrdinaryLocked"/> now, because a safety outcome
        /// still owed is not something a newer warning may erase, and so does
        /// an alarm's hand-over.
        ///
        /// <para>This is NOT a silence: it claims nothing about the operator
        /// and does not advance the quiet cohort. It is the process going away
        /// or a test starting from nothing.</para>
        /// </summary>
        public void DiscardAll()
        {
            lock (_lock)
            {
                DiscardAllLocked("all speech state discarded");
                _safety.Reset();
                _alarmPending.Clear();
                _alarmTimer?.Dispose(); _alarmTimer = null; _alarmGeneration++;
                _retryTimer?.Dispose(); _retryTimer = null;
                _lastAlarmRefresh = null;
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
                var handoff = _sink(message, false, intent, level, origin, salvaged: false);
                if (handoff.Reached)
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
                    LedgerAddLocked(message, intent, level, origin, subject, now, handoff.Ticket);

                    // Given to the reader while a train is held: this is one
                    // of the follow-ups the hold exists to let through first,
                    // and the release trace will name it.
                    if (_holdTimer != null) _wentFirst.Add(message);
                }
                return;
            }

            if (intent == SpeechIntent.Urgent)
            {
                // Urgent reaches here only through Urgent(), which has already
                // taken the safety turn — so the current turn IS this call's,
                // and rebinding it is right. Anything else presenting the
                // intent takes a turn of its own rather than sounding with
                // nobody owning the channel, and must never be bound onto an
                // ALARM's turn: that is the merge defect in miniature.
                SafetyTurn? held = _safety.Current;
                SafetyTurn mine = held != null && held.Class == SafetyClass.TransmitSafety && !held.Bound
                    ? held
                    : _safety.ReserveForSafety(subject, now);
                EmitUrgentLocked(message, level, origin, subject, additive, now, mine);
                return;
            }

            var sounding = _sink(message, true, intent, level, origin, salvaged: false);
            if (!sounding.Reached)
            {
                // Suppressed or no backend: the reader never saw the cancel,
                // so its queue — and our ledger — stand untouched.
                _safety.NoteBackendFailed();
                return;
            }

            // The interrupt flushed the reader. Everything believed unspoken
            // is gone from its queue and must be re-queued, in order, behind
            // the interrupter — but NOT in this call (#507). The interrupter
            // is the lead of an action whose own follow-ups are about to be
            // queued by the same handler, and the backlog belongs behind
            // those. It is judged now, held through the settle window, and
            // handed over by ReleaseHeld. Urgent skips all of it on purpose —
            // its own branch below ledgers the warning and nothing else.
            //
            // A TRACKED interrupter does not touch the busy-until: the
            // channel will say when it finished, and the estimate path is
            // for entries nobody will report on.
            if (!sounding.Tracked)
                _readerBusyUntilUtc = now.AddMilliseconds(EstimateLocked(message));

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

            // **Only what THIS interrupt is a recovery opportunity for (#606).**
            // Sweeping the whole ledger is what made an unrelated keypress
            // acknowledgement into permission to replay an old string. An
            // entry the transport says was cut part-way by something that is
            // not us stays exactly where it is: its cause is unknown, this
            // interrupt did not cause it, and an interrupt is not evidence
            // about it.
            var salvage = new List<BelievedQueued>(_believedQueued.Count);
            var declined = new List<BelievedQueued>();
            foreach (var e in _believedQueued)
            {
                if (RecoveryPermitted(e)) salvage.Add(e); else declined.Add(e);
            }
            _believedQueued.Clear();
            _believedQueued.AddRange(declined);

            if (declined.Count > 0)
            {
                Tracing.TraceLine(
                    $"SpeechArbiter: '{Clip(message)}' is not a recovery opportunity for "
                    + $"{declined.Count} owed utterance(s) — they were cut by something that is not us and "
                    + $"their cause is unknown, so an unrelated interrupt does not replay them: {Quote(declined)}",
                    TraceLevel.Info);
            }

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
        /// The safety warning's own hand-over: retain the fact, THEN try to
        /// say it.
        ///
        /// <para><b>The order is the point (#606, Astra section 2).</b> The
        /// old code called the sink first and returned without ledgering
        /// anything when the handoff did not reach a backend — and
        /// <c>ScreenReaderOutput.EmitCore</c> has three real not-reached cases:
        /// speech suppressed, no backend, and the reader refusing. In every
        /// one of them an interrupted transmit-cut sentence left NOTHING
        /// behind: no obligation, no pending state, one Warning line in a trace
        /// nobody is reading at the moment the radio stops transmitting. The
        /// fact is admitted before the first handoff now, so a refusal
        /// downgrades the DELIVERY and never the fact.</para>
        /// </summary>
        private void EmitUrgentLocked(string message, VerbosityLevel? level, string? origin,
            string? subject, bool additive, DateTime now, SafetyTurn turn)
        {
            // The ordinary backlog is already gone — Urgent() discarded it
            // before silencing the backend — and this repeats the removal so
            // the policy is explicit rather than an artifact of call order.
            // What is NOT removed is any other protected obligation: clearing
            // runnable text for a cut must not erase a safety outcome still
            // owed.
            RemoveOrdinaryLocked();

            // A newer safety episode retires an unheard older one on the SAME
            // subject — the one lifecycle event that may, and the reason a
            // subject is an owner rather than a label. An episode with no
            // declared owner retires nothing and is retired by nothing; it is
            // given no house subject, because one broad subject would let a
            // temperature warning retire a reflected-power cut.
            if (!additive) MarkSupersededLocked(subject, $"'{message}'", origin, now);

            BelievedQueued? owed = AdmitProtectedLocked(message, level, origin, subject, now);

            var sounding = _sink(message, true, SpeechIntent.Urgent, level, origin, salvaged: false);
            if (!sounding.Reached)
            {
                _safety.Abandon(turn, "the reader did not take the safety warning (suppressed, refused or no backend)");
                if (owed != null)
                {
                    owed.State = Owed.NeverStarted;
                    owed.AttemptsWithoutProgress++;
                    owed.AutomaticAttempts++;
                }
                Tracing.TraceLine(
                    "SpeechArbiter: the reader did not take a SAFETY warning (suppressed, refused or no backend): "
                    + $"'{Clip(message)}'"
                    + (owed != null
                        ? ". It is retained as an undelivered obligation and is owed in full."
                        : ". Nothing could be retained for it — see the refusal above."),
                    TraceLevel.Error);
                return;
            }

            if (!sounding.Tracked)
                _readerBusyUntilUtc = now.AddMilliseconds(EstimateLocked(message));

            if (owed != null)
            {
                owed.Ticket = sounding.Ticket;
                owed.State = Owed.Pending;
                owed.AutomaticAttempts++;
                // A tracked entry is retired by the reader's own answer; an
                // untracked one by nothing at all, because for a protected
                // obligation an elapsed estimate is delivery-unknown rather
                // than delivery. Either way the clock does not discharge it.
                owed.EstFinishUtc = DateTime.MaxValue;
            }

            _safety.Bind(turn, sounding, EstimateLocked(message), now);
        }

        /// <summary>
        /// Register a safety obligation before anything is said about it, or
        /// refuse visibly when the slot ceiling is reached. Returns the entry,
        /// or null when nothing could be retained.
        /// </summary>
        private BelievedQueued? AdmitProtectedLocked(string message, VerbosityLevel? level,
            string? origin, string? subject, DateTime now)
        {
            int protectedNow = 0;
            foreach (var e in _believedQueued) if (e.Protected) protectedNow++;
            if (protectedNow >= ProtectedSlotCap)
            {
                Tracing.TraceLine(
                    $"SpeechArbiter: the safety obligation ceiling of {ProtectedSlotCap} is full, so this warning "
                    + $"is spoken but NOT retained: '{Clip(message)}'"
                    + (subject != null ? $" [subject '{subject}']" : " [no subject declared]")
                    + $". {protectedNow} obligation(s) are already owed: {Quote(_believedQueued)}. "
                    + "Refusing the registration is deliberate — evicting an unheard warning would claim it had "
                    + "been delivered.",
                    TraceLevel.Error);
                return null;
            }

            var entry = new BelievedQueued
            {
                Message = message,
                Intent = SpeechIntent.Urgent,
                Level = level,
                Origin = origin,
                Subject = subject,
                FirstEmittedUtc = now,
                SalvageCount = 0,
                Ticket = 0,
                Protected = true,
                QuietGeneration = _safety.QuietGeneration,
                // Not on the estimate path: admitted before any handoff, it
                // occupies nothing yet, and stacking an estimate onto the
                // reader's busy-until here would charge for speech that has
                // not been attempted.
                EstFinishUtc = DateTime.MaxValue,
            };
            LedgerInsertLocked(entry);
            return entry;
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
                // The oldest ORDINARY entry, never a protected one: queue
                // overflow may not retire a safety obligation (#606).
                int victim = _held.FindIndex(e => !e.Protected);
                if (victim >= 0)
                {
                    var oldest = _held[victim];
                    _held.RemoveAt(victim);
                    Tracing.TraceLine(
                        $"SpeechArbiter: dropped a salvage (held set full at {LedgerCap}, oldest first) "
                        + $"after {oldest.SalvageCount} rescue(s): '{oldest.Message}'",
                        TraceLevel.Warning);
                }
            }
            _held.Add(entry);
        }

        /// <summary>
        /// Is this interrupt a recovery opportunity for this obligation
        /// (#606)? Everything except an entry the transport says was cut
        /// part-way by a cause nobody can name — and a protected obligation
        /// whose automatic attempts have backed off, which is owed and
        /// waiting rather than declined.
        ///
        /// Note what is NOT asked here: how old it is, or whether an interrupt
        /// happened. Age is asked later, by
        /// <see cref="SalvageRefusalLocked"/>, and the interrupt is the
        /// occasion rather than the permission.
        /// </summary>
        /// <para>A PROTECTED obligation is eligible whatever cut it. Ordinary
        /// navigation may not take a safety outcome's slot, so an interrupt
        /// that arrives while one is still owed puts it back rather than
        /// inheriting it — and the hazard has not ceased because a sentence
        /// was cut by something nobody can name.</para>
        ///
        /// <para><b>Except when the operator stopped it (#182).</b> Keeping a
        /// safety fact through a Silence was right and was only half the rule:
        /// the very next unrelated interrupt treated it as recoverable and
        /// handed it back 600 ms later, which is the same defiance of the
        /// shut-up key in a better-named place. An obligation admitted under an
        /// older QUIET COHORT is owed, reachable and not automatically
        /// re-offered. Ctrl stops the cohort's speech; it acknowledges nothing
        /// and disables nothing.</para>
        ///
        /// <para><b>What is still NOT asked here, and it is deliberate.</b>
        /// Sol's review names a second validity question: whether the
        /// obligation still belongs in the RECEIVING CONTEXT. A zero-mark
        /// connect clause is handed over behind JJ or Escape merely because
        /// they happened, and if the connect phase has moved on since, that
        /// delivers an obsolete first hearing inside an unrelated action.
        /// Every obligation now carries the two things such a rule needs — an
        /// owner (its subject) and a generation — but <b>no owner advances a
        /// context generation yet</b>, so the check would never fire and is
        /// not written. Wiring one is a decision about WHICH events end a
        /// context, and getting it wrong deletes the very clause #606 exists
        /// to deliver: it is reported for a ruling rather than guessed at
        /// here.</para>
        private bool RecoveryPermitted(BelievedQueued e) =>
            !e.AutoRecoveryPaused
            && _safety.InCurrentCohort(e.QuietGeneration)
            && (e.Protected || e.State != Owed.PausedUnknownCause);

        /// <summary>
        /// Everything but the protected obligations leaves the ledger. Used
        /// where the old code cleared it outright — an urgent warning's
        /// discard and the operator's Silence — because neither of those is a
        /// statement that a safety outcome has been heard.
        /// </summary>
        private void RemoveOrdinaryLocked() => _believedQueued.RemoveAll(e => !e.Protected);

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

                // Protected obligations go to the reader FIRST. Ordinary app
                // speech cannot preempt a safety outcome, and a train handed
                // over in arrival order would queue a transmit-cut sentence
                // behind whatever ordinary narration happened to be older.
                var train = new List<BelievedQueued>(_held.Count);
                foreach (var s in _held) if (s.Protected) train.Add(s);
                foreach (var s in _held) if (!s.Protected) train.Add(s);
                _held.Clear();
                int handed = 0;
                bool protectedHandedOver = false;
                foreach (var s in train)
                {
                    string? refusal = SalvageRefusalLocked(s, now, atRescue: false);
                    if (refusal != null)
                    {
                        TraceDropLocked(s, refusal, now);
                        continue;
                    }

                    // A protected obligation that has come back saying nothing
                    // at all, over and over, stops chasing interrupts — and is
                    // kept, owed, rather than dropped. The distinction matters:
                    // "we have stopped trying for now" and "nothing is owed"
                    // are different states and only one of them is true.
                    if (s.Protected
                        && (s.AttemptsWithoutProgress >= ProtectedAttemptsWithoutProgress
                            || !s.AutomaticBudgetLeft))
                    {
                        s.AutoRecoveryPaused = true;
                        LedgerEnterLocked(s, now);
                        Tracing.TraceLine(
                            "SpeechArbiter: a SAFETY obligation is still undelivered and its automatic "
                            + $"attempts have backed off after {s.AutomaticAttempts} hand-over(s), "
                            + $"{s.AttemptsWithoutProgress} of them consecutively saying nothing at all: "
                            + $"'{s.Message}'. It remains owed and waits for an explicit replay or a "
                            + "backend-recovery edge.",
                            TraceLevel.Error);
                        continue;
                    }

                    // **A protected retry takes the safety speaking turn, like
                    // its first attempt did (#611, Sol's Track K review
                    // section 1).** It used to be requeued here with
                    // interrupt:false and no reservation at all, so it entered
                    // the pump as ordinary work: alarms waiting their turn saw
                    // a free token and went ahead of it, and anything already
                    // in the FIFO was spoken first. Sorting the held set put
                    // protected entries first in the TRAIN and governed nothing
                    // outside it. Now the first protected entry in a release
                    // reserves the turn and interrupts, exactly as Urgent
                    // does; a second protected entry in the same release is
                    // queued behind the first rather than cutting it, because
                    // two safety sentences interrupting each other is the very
                    // thing the coordinator exists to stop.
                    SafetyTurn? turn = null;
                    bool interruptForSafety = false;
                    if (s.Protected)
                    {
                        interruptForSafety = !protectedHandedOver;
                        if (interruptForSafety) turn = _safety.ReserveForSafety(s.Subject, now);
                    }

                    var requeued = _sink(s.Message, interruptForSafety,
                        interruptForSafety ? SpeechIntent.Urgent : s.Intent, s.Level, s.Origin, salvaged: true);
                    if (!requeued.Reached)
                    {
                        _safety.NoteBackendFailed();
                        if (turn != null) _safety.Abandon(turn, "the reader did not take a safety obligation's retry");

                        // **A protected obligation is RE-ENTERED here (#606).**
                        // It used to be dropped on the floor with one Warning
                        // line, which is the same hole the initial handoff had:
                        // a retry that cannot reach speech was erasing the fact
                        // it was retrying. An ordinary entry still leaves,
                        // because it occupies nothing and nothing is owed for
                        // it beyond this attempt.
                        if (s.Protected)
                        {
                            s.State = Owed.NeverStarted;
                            s.AttemptsWithoutProgress++;
                            s.AutomaticAttempts++;
                            s.Ticket = 0;
                            s.EstFinishUtc = DateTime.MaxValue;
                            LedgerInsertLocked(s);
                            Tracing.TraceLine(
                                "SpeechArbiter: the reader did not take a SAFETY obligation's retry (suppressed, "
                                + $"refused or no backend) after {s.SalvageCount} rescue(s): '{s.Message}'. "
                                + "It stays owed in full — the reader took nothing, so none of it has been heard.",
                                TraceLevel.Error);
                            continue;
                        }

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
                    // by the count it carries with it. A fresh ticket, because
                    // this hand-over is a new question to the reader.
                    //
                    // **A rescue is spent only on an attempt that said
                    // something (#606).** The cap bounds repeats the operator
                    // may actually have heard, and an attempt withdrawn before
                    // the reader started it is not one of those — it was
                    // spending the budget for a hearing that never happened,
                    // which is how a clause saying the station restore was
                    // unconfirmed exhausted both its rescues without ever
                    // being spoken once.
                    //
                    // And the marks are kept rather than zeroed. The old line
                    // threw away the only record of how far the obligation had
                    // ever got, which is the fact that says whether handing it
                    // over again is a repeat or a first hearing.
                    // The exemption needs POSITIVE evidence that no attempt
                    // ever began — the transport saying so. An entry nobody
                    // can report on keeps the old accounting exactly: with no
                    // delivery data the honest assumption is still that the
                    // reader started it, which is the whole pre-#521 contract
                    // and what bounds an untracked rescue.
                    bool neverBegun = s.State == Owed.NeverStarted && s.MarksReachedEver == 0;
                    if (!neverBegun) s.SalvageCount++;
                    if (s.Protected) s.AutomaticAttempts++;
                    s.Ticket = requeued.Ticket;
                    s.MarksReached = 0;
                    s.LastOutcome = null;
                    s.State = Owed.Pending;
                    if (s.Protected)
                    {
                        // The clock does not discharge a safety obligation,
                        // tracked or not: an elapsed estimate is delivery
                        // unknown, which is recorded rather than converted
                        // into evidence of hearing.
                        LedgerInsertLocked(s);
                        s.EstFinishUtc = DateTime.MaxValue;
                        if (turn != null)
                        {
                            _safety.Bind(turn, requeued, EstimateLocked(s.Message), now);
                            protectedHandedOver = true;
                        }
                        if (!requeued.Tracked) _readerBusyUntilUtc = now.AddMilliseconds(EstimateLocked(s.Message));
                    }
                    else LedgerEnterLocked(s, now);
                    handed++;
                    if (neverBegun)
                    {
                        Tracing.TraceLine(
                            "SpeechArbiter: handed over again without spending a rescue — no attempt on this "
                            + $"one has ever said a word, so this is a first hearing: '{Clip(s.Message)}'"
                            + (s.Subject != null ? $" [subject '{s.Subject}']" : string.Empty),
                            TraceLevel.Info);
                    }
                }

                Tracing.TraceLine(
                    $"SpeechArbiter: released {handed} of {train.Count} held salvage(s) {heldMs} ms after "
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
        /// <param name="keepProtected">
        /// True where the caller is silencing or discarding ORDINARY work: a
        /// protected obligation held here goes back to the ledger rather than
        /// being let go, because neither a Silence nor an urgent warning is a
        /// statement that a safety outcome has been heard (#606).
        /// </param>
        private void EndHoldLocked(string? letGoReason, bool keepProtected = false)
        {
            bool wasHolding = _holdTimer != null;
            _holdTimer?.Dispose();
            _holdTimer = null;
            if (wasHolding) _holdGeneration++;

            if (keepProtected)
            {
                for (int i = _held.Count - 1; i >= 0; i--)
                {
                    if (!_held[i].Protected) continue;
                    var kept = _held[i];
                    _held.RemoveAt(i);
                    LedgerEnterLocked(kept, _clock.UtcNow);
                    Tracing.TraceLine(
                        $"SpeechArbiter: a SAFETY obligation was held when {letGoReason ?? "the hold ended"}; "
                        + $"it goes back to the ledger still owed rather than being let go: '{Clip(kept.Message)}'",
                        TraceLevel.Info);
                }
            }

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

        private static string Clip(string s) => s.Length > 60 ? s.Substring(0, 60) + "…" : s;

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

            // A safety obligation is exempt from every bound below. Ordinary
            // verbosity, navigation, a timer expiring, queue overflow and a
            // rescue count running out may not retire it; only its owner's own
            // newer statement, checked immediately above, may (#606). What
            // still governs it is evidence — it is not recovered at all
            // without something saying the unit was lost.
            if (entry.Protected) return null;

            if (entry.SalvageCount >= MaxSalvages)
                return $"salvage cap: already rescued {entry.SalvageCount} times, limit {MaxSalvages}";

            if (ageMs > SalvageCeilingMs)
                return $"ceiling: {ageMs} ms old against the {SalvageCeilingMs} ms lifetime"
                    + (entry.Subject != null ? ", never superseded" : string.Empty);

            // The word-count bound asks "has this been on the reader long
            // enough that it was probably heard by now?" — and an attempt that
            // never said a word certainly was not. Asking it of an obligation
            // the transport says never began is the same error as spending a
            // rescue on one: charging for a hearing that did not happen. The
            // ceiling above still bounds it, so nothing lives forever (#606).
            if (entry.MarksReachedEver == 0 && entry.State == Owed.NeverStarted) return null;

            if (atRescue && entry.Subject == null)
            {
                int boundMs = EstimateLocked(entry.Message) * SalvageAgeMultiple;
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
            WithdrawSupersededFromPumpLocked(now);
        }

        /// <summary>
        /// Reach past the ledger into the delivery pump: a superseded entry
        /// the pump still holds UNSENT is taken back, and its ledger entry
        /// goes with it, because it will now never be spoken (#606).
        ///
        /// <para>Supersession reached the ledger and the held set and stopped
        /// there, so it governed what would be RESCUED and not what was about
        /// to be said. That is why a progress heartbeat could still be spoken
        /// immediately behind the line that answered it, and why a dialog's
        /// title could arrive after the dialog had been replaced. The pump
        /// owns the unsent text since #521; text still inside it can be taken
        /// back, and the older rationale that it could not is simply out of
        /// date.</para>
        ///
        /// <para>No outcome is reported for a withdrawal: the entry leaves the
        /// ledger here, in the same breath, so there is nothing left for an
        /// outcome to account for — and reporting one would re-enter this
        /// class from the pump's own thread mid-supersession.</para>
        /// </summary>
        private void WithdrawSupersededFromPumpLocked(DateTime now)
        {
            if (_withdrawUnsent == null) return;
            for (int i = _believedQueued.Count - 1; i >= 0; i--)
            {
                var e = _believedQueued[i];
                if (e.SupersededBy == null || e.Ticket == 0) continue;
                if (e.State != Owed.Pending) continue;   // the pump cannot still be holding it
                bool taken;
                try { taken = _withdrawUnsent(e.Ticket, $"superseded by {e.SupersededBy}"); }
                catch { taken = false; }
                if (!taken) continue;
                _believedQueued.RemoveAt(i);
                TraceDropLocked(e, "taken back from the delivery queue before it was said, "
                    + $"superseded by {e.SupersededBy}", now);
            }
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

        /// <summary>
        /// A first entry into the ledger for an ORDINARY queued utterance:
        /// this is emission number one.
        ///
        /// <para>It used to take the protected warning too, with two flags for
        /// the ways that case differed. A safety obligation is now admitted by
        /// <see cref="AdmitProtectedLocked"/> BEFORE its first handoff, which
        /// is a different question asked at a different moment, so the flags
        /// went with it rather than staying here as parameters no caller
        /// ever sets.</para>
        /// </summary>
        private void LedgerAddLocked(string message,
            SpeechIntent? intent, VerbosityLevel? level, string? origin, string? subject, DateTime now,
            long ticket)
        {
            var entry = new BelievedQueued
            {
                Message = message,
                Intent = intent,
                Level = level,
                Origin = origin,
                Subject = subject,
                FirstEmittedUtc = now,
                SalvageCount = 0,
                Ticket = ticket,
                Protected = false,
                QuietGeneration = _safety.QuietGeneration,
            };
            LedgerEnterLocked(entry, now);
        }

        /// <summary>
        /// Put an entry into the ledger and — for an UNTRACKED entry — stack
        /// its estimated speaking time onto the reader's believed busy-until.
        ///
        /// A re-entering salvage brings its own <c>FirstEmittedUtc</c> and
        /// <c>SalvageCount</c> with it. That is the fix for #273 in one line:
        /// the lease is renewed, as it must be — the reader really is going to
        /// be busy that long again — but the entry's AGE and its rescue count
        /// are not, so the thing that justifies the next rescue is no longer
        /// manufactured by the last one.
        ///
        /// A TRACKED entry (ticket set) gets no estimate at all (#521). Its
        /// finish is <see cref="DateTime.MaxValue"/> so the clock cannot
        /// retire it, and it leaves nothing on the busy-until, because the
        /// busy-until is the estimate path's stack and this entry is not on
        /// that path. The reader's answer arrives through
        /// <see cref="OnOutcome"/> and does the retiring.
        /// </summary>
        private void LedgerEnterLocked(BelievedQueued entry, DateTime now)
        {
            if (entry.Ticket != 0)
            {
                entry.EstFinishUtc = DateTime.MaxValue;
            }
            else
            {
                var start = _readerBusyUntilUtc > now ? _readerBusyUntilUtc : now;
                var finish = start.AddMilliseconds(EstimateLocked(entry.Message));
                _readerBusyUntilUtc = finish;
                entry.EstFinishUtc = finish;
            }

            LedgerInsertLocked(entry);
        }

        /// <summary>
        /// Put an entry in, evicting the oldest ORDINARY one on overflow — a
        /// protected obligation is never evicted by queue pressure (#606). A
        /// ledger this deep is itself the bug the #197 transcript rule exists
        /// to catch, so this stays purely defensive.
        /// </summary>
        private void LedgerInsertLocked(BelievedQueued entry)
        {
            if (_believedQueued.Count >= LedgerCap)
            {
                int victim = _believedQueued.FindIndex(e => !e.Protected);
                if (victim >= 0) _believedQueued.RemoveAt(victim);
            }
            _believedQueued.Add(entry);
        }

        private void PruneLedgerLocked(DateTime now)
        {
            // The is-speaking correction (#557): if the backend can say it is
            // NOT speaking while the estimate says it should be, the estimate
            // is wrong in the direction that keeps stale entries alive, and
            // every untracked entry is retired now. Asked only when there is
            // something to correct — no RPC for an empty ledger — and never
            // waited on: a null answer means "cannot say" and the estimate
            // stands. Tracked entries are untouched; their reader is the one
            // answering for them.
            if (_isSpeaking != null && _readerBusyUntilUtc > now && _believedQueued.Exists(e => e.Ticket == 0))
            {
                bool? speaking = null;
                try { speaking = _isSpeaking(); } catch { /* a probe that throws is a probe that cannot say */ }
                if (speaking == false)
                {
                    int early = (int)(_readerBusyUntilUtc - now).TotalMilliseconds;
                    int retired = 0;
                    foreach (var e in _believedQueued)
                    {
                        if (e.Ticket == 0 && e.EstFinishUtc > now) { e.EstFinishUtc = now; retired++; }
                    }
                    _readerBusyUntilUtc = now;
                    Tracing.TraceLine(
                        $"SpeechArbiter: the reader says it is not speaking, {early} ms before the estimate "
                        + $"said it would be; {retired} estimated entr{(retired == 1 ? "y" : "ies")} retired early.",
                        TraceLevel.Info);
                }
            }

            // Estimated-finished utterances leave the ledger; salvaging them
            // would repeat speech the operator (probably) heard. A tracked
            // entry's finish is MaxValue and never passes.
            //
            // **A PROTECTED obligation never leaves on an estimate (#611).**
            // For an untracked backend there is no reporter at all, so an
            // elapsed estimate says only "we assume the channel is free
            // again". Letting it also discharge the fact would convert a timer
            // into evidence of hearing, which is the one conversion this
            // contract forbids — and it is exactly how a warning could vanish
            // on a desk where nothing can report. It is recorded as delivery
            // UNKNOWN instead, which is what it is.
            for (int i = _believedQueued.Count - 1; i >= 0; i--)
            {
                var e = _believedQueued[i];
                if (e.EstFinishUtc > now) continue;
                if (!e.Protected) { _believedQueued.RemoveAt(i); continue; }
                if (e.DeliveryUnknown) continue;
                e.DeliveryUnknown = true;
                e.EstFinishUtc = DateTime.MaxValue;
                Tracing.TraceLine(
                    "SpeechArbiter: a SAFETY obligation's occupancy estimate elapsed with nobody able to report "
                    + $"on it; delivery is UNKNOWN and it stays owed: '{Clip(e.Message)}'"
                    + (e.Subject != null ? $" [subject '{e.Subject}']" : string.Empty),
                    TraceLevel.Warning);
            }

            // **And the ceiling retires a tracked entry too now (#606).** A
            // tracked entry is stored with an infinite estimated finish, so
            // the line above can never reach one — which is right while the
            // reader still owes us an answer, and wrong once it has given one
            // and the answer was "cut". Such an entry waited indefinitely for
            // any interrupt to arrive and judge it, and that single fact is
            // what made an unrelated keypress able to revive a sentence from
            // minutes ago. An entry that has been answered and is past its
            // lifetime leaves here, on its own, with a line saying it expired
            // unheard — a salvage that gives up silently is the defect every
            // bound in this class exists to end. Protected obligations are
            // exempt; an entry still in flight is left to the reader.
            for (int i = _believedQueued.Count - 1; i >= 0; i--)
            {
                var e = _believedQueued[i];
                if (e.Protected || e.State == Owed.Pending) continue;
                int ageMs = (int)(now - e.FirstEmittedUtc).TotalMilliseconds;
                if (ageMs <= SalvageCeilingMs) continue;
                _believedQueued.RemoveAt(i);
                Tracing.TraceLine(
                    $"SpeechArbiter: an owed utterance expired unheard at {ageMs} ms against the "
                    + $"{SalvageCeilingMs} ms lifetime, {DescribeState(e)}: '{Clip(e.Message)}'"
                    + (e.Subject != null ? $" [subject '{e.Subject}']" : string.Empty),
                    TraceLevel.Warning);
            }
        }

        private static string DescribeState(BelievedQueued e) => e.State switch
        {
            Owed.CutByUs => $"cut by us at word {e.MarksReachedEver} of {e.MarkCount}",
            Owed.NeverStarted => "never begun",
            Owed.PausedUnknownCause =>
                $"paused at word {e.MarksReachedEver} of {e.MarkCount} by a cause nobody can name",
            _ => "still in flight",
        };

        /// <summary>
        /// The reader's answer about one hand-over, by ticket (#521). Called
        /// from the paced delivery's thread with no lock held; takes the
        /// arbiter's lock and nothing else. Every Completed answer, ledgered
        /// or not, teaches the rate model — an interrupter is never in the
        /// ledger, and its duration is as real as anyone's.
        ///
        /// What each answer does to the entry, and why:
        ///
        /// - **Completed** — it leaves the ledger, wherever it is. The
        ///   operator heard it; rescuing it would be the #521 repeat.
        /// - **Cancelled** — it stays, with the last mark recorded. If the
        ///   cut was ours, the interrupt path already moved it to the held
        ///   set and will judge it; if it was not ours — the operator's key,
        ///   a focus change — the entry waits for the NEXT interrupt to judge
        ///   it under the same rules. The arbiter does NOT re-speak on a
        ///   foreign cancel of its own accord: on a live desk the operator's
        ///   keys cancel NVDA constantly, and a rescue per keystroke is #554's
        ///   runaway with a better excuse.
        /// - **Unknown, refused** — it leaves the ledger. The reader took
        ///   nothing, so nothing is occupied and nothing is owed; the trace
        ///   says so loudly because a refusal is a fault in the app or the
        ///   reader's mode, not a normal outcome.
        /// - **Unknown, anything else** — it becomes an UNTRACKED entry on
        ///   the estimate path from now, exactly as if #521 had never landed
        ///   for this one utterance, and the trace says the ledger is
        ///   guessing about it. The type cannot express "spoken" here, and
        ///   that is the point.
        /// </summary>
        public void OnOutcome(long ticket, string message, SpeechOutcome outcome)
        {
            lock (_lock)
            {
                var now = _clock.UtcNow;
                if (outcome.WasHeard) _rate.Observe(message, outcome.ElapsedMs);

                // The one occupancy token, before anything else (#611): only
                // the CURRENT turn's own ticket may end the current turn.
                OnSafetyOutcomeLocked(ticket, outcome, now);

                if (outcome.Kind == SpeechOutcomeKind.Unknown
                    && outcome.UnknownReason == SpeechUnknownReason.Refused)
                {
                    _safety.NoteBackendFailed();
                }

                // **A BACKEND-RECOVERY EDGE, not merely a success (#611).**
                // This used to fire on any delivery completing anywhere, and
                // "an unrelated sentence finished" is the generic-key-
                // acknowledgement defect wearing a different hat: it is not
                // evidence about this obligation, only about the machine. The
                // edge is the transition OUT of a channel known to be refusing
                // or absent, taken exactly once, so duplicate successes cannot
                // replenish a budget. An obligation the operator silenced is
                // skipped: a recovering backend is not permission to overrule
                // the shut-up key (#182).
                if (outcome.WasHeard && _safety.NoteBackendWorkedAndTakeEdge())
                {
                    foreach (var e in _believedQueued)
                    {
                        if (!e.AutoRecoveryPaused) continue;
                        if (!_safety.InCurrentCohort(e.QuietGeneration))
                        {
                            Tracing.TraceLine(
                                "SpeechArbiter: the speech channel recovered, but this SAFETY obligation was "
                                + $"silenced by the operator and stays paused: '{Clip(e.Message)}'",
                                TraceLevel.Info);
                            continue;
                        }
                        e.AutoRecoveryPaused = false;
                        e.AttemptsWithoutProgress = 0;
                        // ONE probe, and the spent-repeat count is not
                        // cleared: a flapping channel cannot manufacture an
                        // unlimited supply of fresh budgets.
                        e.ProbesGranted++;
                        Tracing.TraceLine(
                            "SpeechArbiter: the speech channel recovered after refusing, so the SAFETY obligation "
                            + $"that had backed off gets ONE further attempt (probe {e.ProbesGranted}, "
                            + $"{e.AutomaticAttempts} attempt(s) already spent): '{Clip(e.Message)}'",
                            TraceLevel.Info);
                    }
                }

                var inLedger = true;
                var entry = _believedQueued.Find(e => e.Ticket == ticket && ticket != 0);
                if (entry == null)
                {
                    entry = _held.Find(e => e.Ticket == ticket && ticket != 0);
                    inLedger = false;
                }

                if (entry == null)
                {
                    // An interrupter (never ledgered), or an entry already
                    // retired by supersession, the cap, the ceiling or a
                    // Silence. Nothing to account for; the outcome is still
                    // worth a line, because it is the truth about a delivery.
                    Tracing.TraceLine(
                        $"SpeechArbiter: delivery #{ticket} {outcome} — '{message}' (not in the ledger: an interrupter, or already retired)",
                        TraceLevel.Verbose);
                    return;
                }

                entry.LastOutcome = outcome.Kind;
                switch (outcome.Kind)
                {
                    case SpeechOutcomeKind.Completed:
                        if (inLedger) _believedQueued.Remove(entry); else _held.Remove(entry);
                        Tracing.TraceLine(
                            $"SpeechArbiter: delivery #{ticket} {outcome} — '{message}' left the ledger "
                            + $"{(int)(now - entry.FirstEmittedUtc).TotalMilliseconds} ms after first emission"
                            + (entry.SalvageCount > 0 ? $" ({entry.SalvageCount} rescue(s))" : string.Empty),
                            TraceLevel.Verbose);
                        return;

                    case SpeechOutcomeKind.Cancelled:
                        entry.MarksReached = outcome.MarksReached;
                        entry.MarkCount = outcome.MarkCount;
                        if (outcome.MarksReached > entry.MarksReachedEver)
                            entry.MarksReachedEver = outcome.MarksReached;
                        if (outcome.MarksReached == 0) entry.AttemptsWithoutProgress++;
                        else entry.AttemptsWithoutProgress = 0;

                        // **The one place the cause is written down (#606).**
                        // Zero marks means the reader never started it — it
                        // was withdrawn from our own queue — whoever asked, so
                        // the whole of it is still owed and nothing has been
                        // repeated by saying it. Otherwise: if the cut was
                        // ours we caused the loss and may offer the put-back;
                        // if it was not, the callback cannot tell Ctrl from a
                        // focus change from another program taking the
                        // foreground, so the cause stays unknown and no
                        // unrelated interrupt may treat itself as permission.
                        entry.State =
                            outcome.MarksReached == 0 ? Owed.NeverStarted
                            : outcome.CancelledByUs ? Owed.CutByUs
                            : Owed.PausedUnknownCause;

                        Tracing.TraceLine(
                            $"SpeechArbiter: delivery #{ticket} {outcome} — '{message}' is "
                            + entry.State switch
                            {
                                Owed.NeverStarted =>
                                    "still wholly owed: it never said a word, so a later hand-over is a first "
                                    + "hearing and spends no rescue",
                                Owed.CutByUs =>
                                    "owed from where it was cut; we cut it, so the put-back is ours to offer",
                                _ =>
                                    "owed and PAUSED: the cause of the cut is unknown, so an unrelated later "
                                    + "interrupt is not permission to replay it",
                            }
                            + (inLedger ? string.Empty : " (held; the settle window will judge it)"),
                            TraceLevel.Info);
                        return;

                    default:
                        if (outcome.UnknownReason == SpeechUnknownReason.Refused)
                        {
                            // **INVERTED 2026-09-23 (#606).** This used to
                            // remove the entry and trace "nothing is owed",
                            // and a test asserted that as the contract. A
                            // refusal is the reader taking NOTHING — NVDA
                            // asleep for the focused application — which
                            // means the operator heard none of it, which
                            // means all of it is still owed. "The delivery
                            // failed" and "there was nothing to deliver" are
                            // different facts, and only one of them was true.
                            entry.State = Owed.NeverStarted;
                            entry.AttemptsWithoutProgress++;
                            Tracing.TraceLine(
                                $"SpeechArbiter: delivery #{ticket} {outcome} — '{message}' stays owed: "
                                + "the reader took nothing, so none of it has been heard",
                                TraceLevel.Warning);
                            return;
                        }

                        // Back to the estimate path, from now, as an untracked entry.
                        entry.Ticket = 0;
                        var start = _readerBusyUntilUtc > now ? _readerBusyUntilUtc : now;
                        var finish = start.AddMilliseconds(EstimateLocked(message));
                        _readerBusyUntilUtc = finish;
                        entry.EstFinishUtc = finish;
                        Tracing.TraceLine(
                            $"SpeechArbiter: delivery #{ticket} {outcome} — '{message}' is back on the ESTIMATE: "
                            + $"the ledger is guessing it finishes {(int)(finish - now).TotalMilliseconds} ms from now",
                            TraceLevel.Warning);
                        return;
                }
            }
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
            ClearTransientLocked();
            _believedQueued.Clear();
            _readerBusyUntilUtc = DateTime.MinValue;
            EndHoldLocked(reason);
        }

        /// <summary>
        /// An urgent warning's discard: everything ordinary goes, and a
        /// protected obligation still owed does not (#606). Clearing the
        /// runnable text for a cut must not erase the cut, nor another safety
        /// outcome the operator has still not heard.
        /// </summary>
        private void DiscardOrdinaryLocked(string reason)
        {
            ClearTransientLocked();
            KeepProtectedLocked(reason);
            _readerBusyUntilUtc = DateTime.MinValue;
            EndHoldLocked(reason, keepProtected: true);
        }

        /// <summary>Everything but the protected obligations leaves the ledger, each ordinary drop traced as a set.</summary>
        private void KeepProtectedLocked(string reason)
        {
            int before = _believedQueued.Count;
            RemoveOrdinaryLocked();
            int kept = _believedQueued.Count;
            if (kept > 0)
            {
                Tracing.TraceLine(
                    $"SpeechArbiter: {before - kept} ordinary utterance(s) forgotten because {reason}; "
                    + $"{kept} SAFETY obligation(s) kept, still owed: {Quote(_believedQueued)}",
                    TraceLevel.Info);
            }
        }

        private void ClearTransientLocked()
        {
            foreach (var entry in _pending.Values) entry.Timer?.Dispose();
            _pending.Clear();

            // Forget what was last spoken as well, so the next value after
            // an urgent warning always speaks rather than being suppressed
            // as a duplicate of something the flush just discarded.
            _lastByKey.Clear();
        }
    }
}
