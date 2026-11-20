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
    /// <b>Heard in full, or nothing opens (repair 1; Codex's check of
    /// 83932446, C2).</b> The words are followed on the count's poll and
    /// ceiling (<see cref="CountdownRun.SpeechPollMs"/>,
    /// <see cref="CountdownRun.SpeechCeilingMs"/>) through a handle that
    /// counts them said only when the reader reported them completed and they
    /// were never cut (<c>CountdownSpeech.SayInFull</c>); on the estimate path,
    /// at their estimated finish, held for the margin when they are the only
    /// warning. Words cut part-way, taken back, never reaching the reader, or
    /// not said within the ceiling open NOTHING, every time, whether or not a
    /// tone would follow: Track Q3 let a start with its tone on go ahead
    /// without its words, and the ruling is that "Recording" is said in full
    /// first, every time. The caller says why it did not open.
    /// </para>
    /// </remarks>
    public static class SaidInFull
    {
        /// <summary>How the wait for one sentence ended.</summary>
        public enum Outcome
        {
            /// <summary>The reader finished it, in full.</summary>
            Said,
            /// <summary>Not heard in full: cut, taken back, never reaching the reader, or not said in time. Nothing may open.</summary>
            NotSaid,
            /// <summary>The start was called off while it waited; the words were taken back.</summary>
            NotWanted,
        }

        /// <summary>True when the start may go on after <paramref name="outcome"/>.</summary>
        public static bool LetsItOpen(Outcome outcome) => outcome == Outcome.Said;

        /// <summary>
        /// Say <paramref name="text"/> and wait until it has been said in full.
        /// </summary>
        /// <param name="say">Queues the words and returns their handle (in the window, the countdown's speech on the recorder's subject).</param>
        /// <param name="withdraw">Takes back the words not yet said; given plain prose for the trace.</param>
        /// <param name="text">The words.</param>
        /// <param name="onlyWarning">True when no tone follows, so these words are the only warning: on the estimate path they are then held for the margin. It no longer changes whether a start opens.</param>
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
                    Withdraw(withdraw, "its words not being heard in full");
                    string why = failed ? "were cut, taken back or never reached the reader"
                                        : "were not said in full within " + CountdownRun.SpeechCeilingMs + " ms";
                    Tracing.TraceLine("SaidInFull: the words before opening " + why + ", so nothing opens", TraceLevel.Warning);
                    return Outcome.NotSaid;
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
