using System;
using System.Threading;
using JJTrace;
using System.Diagnostics;

namespace Radios
{
    /// <summary>
    /// Gives a slow operation a voice, and keeps giving it one until the
    /// operation finishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Silence is what a hung application sounds like.
    /// A sighted operator watching a window fill in knows work is happening; a
    /// blind operator gets nothing, and nothing is indistinguishable from a
    /// crash. Measured 2026-08-24 from the app's own speech transcripts: the
    /// gap between the launch greeting and the first word about radios was
    /// 5.6, 6.0 and 6.0 seconds across three consecutive sessions. Six seconds
    /// is a long time to wonder whether you should force-quit.
    /// </para>
    /// <para>
    /// <b>Why speech rather than a UIA live region.</b> Noel's ruling,
    /// 2026-08-24: "I'd just speak… that way you can do the repeat." A live
    /// region hands the screen reader a value and lets IT decide whether and
    /// when to say anything; the repeat — which is the part that actually
    /// distinguishes "slow" from "stopped" — would not be ours to control.
    /// </para>
    /// <para>
    /// <b>The first line names the work; the repeats say "still".</b> A repeat
    /// identical to the opening line is ambiguous — it could be a fresh start
    /// of a second operation. "Still looking" cannot be misheard that way, and
    /// it is the word that carries the reassurance.
    /// </para>
    /// <para>
    /// <b>It cannot outlive its operation.</b> There is a hard ceiling as well
    /// as a <see cref="Stop"/>, because a progress voice that leaks is worse
    /// than the silence it replaced: it would go on reassuring an operator
    /// about work that finished or failed minutes ago. If the ceiling is
    /// reached, that is said out loud too — reaching it means something took
    /// far longer than anyone expected, which is exactly when an operator most
    /// needs to be told.
    /// </para>
    /// <para>
    /// One at a time, deliberately. Two overlapping progress voices would talk
    /// over each other, and there is no case where two slow operations should
    /// both be narrating.
    /// </para>
    /// </remarks>
    public static class ProgressVoice
    {
        private static readonly object Gate = new object();
        private static Timer _timer;
        private static string _stillTerse;
        private static string _stillChatty;
        private static string _what;
        private static long _startedAtTicks;
        private static int _repeats;
        private static int _maxMs;

        /// <summary>
        /// How long to wait before the first "still working" line, and between
        /// them after that.
        /// </summary>
        /// <remarks>
        /// Four seconds against a measured six-second launch wait means the
        /// operator hears the opening line, then exactly one reassurance before
        /// the real answer arrives. Short enough to land inside the wait; long
        /// enough that a normal launch does not become a conversation.
        /// </remarks>
        public const int DefaultRepeatMs = 4000;

        /// <summary>Ceiling on the whole announcement, however the work is going.</summary>
        public const int DefaultMaxMs = 45000;

        /// <summary>True while a progress voice is running.</summary>
        public static bool Running
        {
            get { lock (Gate) { return _timer != null; } }
        }

        /// <summary>
        /// Start narrating a slow operation.
        /// </summary>
        /// <param name="what">
        /// Short name of the work, for the trace only. Never spoken.
        /// </param>
        /// <param name="openingTerse">Opening line at Terse verbosity.</param>
        /// <param name="openingChatty">
        /// Opening line at Chatty. Say more here — an operator who has asked
        /// for detail is the one who wants to know WHAT is being waited on.
        /// </param>
        /// <param name="stillTerse">Repeat line at Terse.</param>
        /// <param name="stillChatty">Repeat line at Chatty.</param>
        /// <param name="repeatMs">Gap before and between repeats.</param>
        /// <param name="maxMs">Hard ceiling; the voice stops itself here.</param>
        public static void Start(string what,
                                 string openingTerse, string openingChatty,
                                 string stillTerse, string stillChatty,
                                 int repeatMs = DefaultRepeatMs,
                                 int maxMs = DefaultMaxMs)
        {
            lock (Gate)
            {
                // A second start replaces the first rather than stacking.
                StopLocked("superseded by " + what);

                _what = what ?? "";
                _stillTerse = stillTerse;
                _stillChatty = stillChatty;
                _startedAtTicks = Stopwatch.GetTimestamp();
                _repeats = 0;
                _maxMs = maxMs;

                Tracing.TraceLine("ProgressVoice: start '" + _what + "', repeating every "
                    + repeatMs + " ms, ceiling " + maxMs + " ms", TraceLevel.Info);

                Say(openingTerse, openingChatty);

                _timer = new Timer(Tick, null, repeatMs, repeatMs);
            }
        }

        /// <summary>
        /// The operation finished. Stops the voice; says nothing itself,
        /// because whatever finished is about to speak for itself.
        /// </summary>
        /// <remarks>
        /// Safe to call when nothing is running, and safe to call from any
        /// thread — both are true of the paths that call it, which end an
        /// operation from wherever it happened to complete.
        /// </remarks>
        public static void Stop(string reason = "")
        {
            lock (Gate) { StopLocked(reason); }
        }

        private static void StopLocked(string reason)
        {
            if (_timer == null) return;
            _timer.Dispose();
            _timer = null;
            Tracing.TraceLine("ProgressVoice: stop '" + _what + "' after "
                + ElapsedMs() + " ms, " + _repeats + " repeat(s)"
                + (string.IsNullOrEmpty(reason) ? "" : " — " + reason), TraceLevel.Info);

            // The operation is over, so its last "still working" line — if
            // the reader never got to it — is worth nothing now, and the
            // speech arbiter must not rescue it behind whatever interrupts
            // next. Said explicitly because the thing that ends a wait is
            // usually not a progress line: it is the dialog that answers it,
            // or the connect completing (#503).
            try
            {
                ScreenReaderOutput.Supersede(Speech.SpeechSubject.Progress,
                    "the end of the wait for '" + _what + "'"
                    + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"));
            }
            catch
            {
                // A stop must never fail because the speech layer did.
            }
        }

        private static void Tick(object state)
        {
            lock (Gate)
            {
                if (_timer == null) return;

                if (ElapsedMs() >= _maxMs)
                {
                    // Say so rather than just going quiet. Going quiet at the
                    // ceiling would recreate the exact silence this class
                    // exists to remove, at the moment it matters most.
                    Say("This is taking longer than expected.",
                        "This is taking longer than expected. It may still finish; "
                        + "if it does not, close and try again.");
                    StopLocked("ceiling reached");
                    return;
                }

                _repeats++;
                Say(_stillTerse, _stillChatty);
            }
        }

        private static long ElapsedMs() =>
            (Stopwatch.GetTimestamp() - _startedAtTicks) * 1000 / Stopwatch.Frequency;

        /// <summary>
        /// Speak the line that suits the operator's verbosity.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Sent at <see cref="VerbosityLevel.Terse"/> whichever text is chosen,
        /// so it survives a Terse operator's filter. The verbosity decides the
        /// WORDING, not whether the operator is told at all: "is this thing
        /// alive" is not a detail somebody opted out of by asking for less
        /// chat.
        /// </para>
        /// <para>
        /// Queued, never interrupting. The operator may still be hearing the
        /// keystroke confirmation that started this very operation, and cutting
        /// that off to say "please wait" would be a poor trade.
        /// </para>
        /// </remarks>
        private static void Say(string terse, string chatty)
        {
            string text = (ScreenReaderOutput.CurrentVerbosity >= VerbosityLevel.Chatty
                           && !string.IsNullOrEmpty(chatty)) ? chatty : terse;
            if (string.IsNullOrEmpty(text)) return;
            // Keyed as progress (#503): each line covers the one before it,
            // and Stop covers the last. A stale "still looking" is worthless
            // by construction and is never rescued past a newer one.
            ScreenReaderOutput.Speak(text, Speech.SpeechIntent.Queue, VerbosityLevel.Terse,
                subject: Speech.SpeechSubject.Progress);
        }
    }
}
