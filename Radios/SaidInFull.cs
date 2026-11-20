using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// One sentence said in full before something opens (Sprint 49 Track Q3):
    /// the audition's "Recording", said after the countdown and before the
    /// microphone, so it is never in the take.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ruled by Noel 2026-11-19 20:26:</b> <i>"say 'Recording' before the
    /// microphone opens, every time, like the check's 'Transmitting'."</i> The
    /// check's "Transmitting" is queued as the key goes down, because a keyed
    /// check records the radio and not this computer's microphone; the
    /// audition records the microphone, so its word must be FINISHED first.
    /// </para>
    /// <para>
    /// <b>The countdown's own delivery rule, not a new one.</b> The words are
    /// followed exactly as a number of the count is (<see cref="ICountdownWords"/>,
    /// <see cref="CountdownRun.SpeechPollMs"/>, <see cref="CountdownRun.SpeechCeilingMs"/>):
    /// finished when the reader says so, or on the estimate path at its
    /// estimated finish, held for the margin when the words are the only
    /// warning. Words that never reach the reader, or are not said within the
    /// ceiling, are taken back; then, as with the count, where a tone follows
    /// and is the warning they are dropped and the start goes on, and where
    /// these words are the only warning nothing opens.
    /// </para>
    /// </remarks>
    public static class SaidInFull
    {
        /// <summary>How the wait for one sentence ended.</summary>
        public enum Outcome
        {
            /// <summary>The reader finished it.</summary>
            Said,
            /// <summary>Not said in time, and taken back; a tone that follows is the warning, so the start goes on.</summary>
            Dropped,
            /// <summary>Not said, and these words were the only warning: nothing may open.</summary>
            NotSaid,
            /// <summary>The start was called off while it waited; the words were taken back.</summary>
            NotWanted,
        }

        /// <summary>True when the start may go on after <paramref name="outcome"/>.</summary>
        public static bool LetsItOpen(Outcome outcome) => outcome is Outcome.Said or Outcome.Dropped;

        /// <summary>
        /// Say <paramref name="text"/> and wait until it has been said in full.
        /// </summary>
        /// <param name="say">Queues the words and returns their handle (in the window, the countdown's speech on the recorder's subject).</param>
        /// <param name="withdraw">Takes back the words not yet said; given plain prose for the trace.</param>
        /// <param name="text">The words.</param>
        /// <param name="onlyWarning">True when no tone follows, so these words are the only warning.</param>
        /// <param name="stillWanted">Asked at every look: false when the start was called off or the window is closing.</param>
        /// <param name="nowMs">The clock, in milliseconds; <see cref="Environment.TickCount64"/> when null.</param>
        /// <param name="delay">The wait between looks; <see cref="Task.Delay(int, CancellationToken)"/> when null.</param>
        public static async Task<Outcome> SayAsync(Func<string, bool, ICountdownWords?> say, Action<string> withdraw,
            string text, bool onlyWarning, Func<bool> stillWanted,
            Func<long>? nowMs = null, Func<int, CancellationToken, Task>? delay = null)
        {
            if (say == null) throw new ArgumentNullException(nameof(say));
            if (withdraw == null) throw new ArgumentNullException(nameof(withdraw));
            if (stillWanted == null) throw new ArgumentNullException(nameof(stillWanted));
            nowMs ??= () => Environment.TickCount64;
            delay ??= Task.Delay;
            if (!stillWanted()) return Outcome.NotWanted;

            ICountdownWords words;
            try { words = say(text, onlyWarning) ?? CountdownWords.Undelivered; }
            catch (Exception ex)
            {
                Tracing.TraceLine("SaidInFull: speech threw, so the words count as never said: " + ex.Message, TraceLevel.Warning);
                words = CountdownWords.Undelivered;
            }

            long from = nowMs();
            while (true)
            {
                // Taken before the questions, so news that lands between asking
                // and waiting still wakes the wait (as the count does).
                Task news = NewsOf(words);
                bool failed = Ask(() => words.Failed, failedIfThrows: true);
                bool said = !failed && Ask(() => words.Finished, failedIfThrows: false);
                if (!stillWanted())
                {
                    Withdraw(withdraw, "the start being called off");
                    return Outcome.NotWanted;
                }
                if (said) return Outcome.Said;
                if (failed || nowMs() - from >= CountdownRun.SpeechCeilingMs)
                {
                    Withdraw(withdraw, "its words not being delivered");
                    string why = failed ? "never reached the reader" : "were not said within " + CountdownRun.SpeechCeilingMs + " ms";
                    Tracing.TraceLine("SaidInFull: the words before opening " + why
                        + (onlyWarning ? "; they were the only warning, so nothing opens" : "; a tone follows, so the start goes on"),
                        TraceLevel.Warning);
                    return onlyWarning ? Outcome.NotSaid : Outcome.Dropped;
                }
                Task poll = delay(CountdownRun.SpeechPollMs, CancellationToken.None);
                if (await Task.WhenAny(poll, news).ConfigureAwait(true) == poll) await poll.ConfigureAwait(true);
            }
        }

        private static Task NewsOf(ICountdownWords words)
        {
            try { return words.NextNews ?? CountdownWords.NoNews; }
            catch (Exception) { return CountdownWords.NoNews; }
        }

        private static bool Ask(Func<bool> question, bool failedIfThrows)
        {
            try { return question(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("SaidInFull: asking about its words threw: " + ex.Message, TraceLevel.Warning);
                return failedIfThrows;
            }
        }

        private static void Withdraw(Action<string> withdraw, string why)
        {
            try { withdraw(why); }
            catch (Exception ex)
            {
                Tracing.TraceLine("SaidInFull: taking its words back threw: " + ex.Message, TraceLevel.Warning);
            }
        }
    }
}
