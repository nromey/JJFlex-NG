using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JJTrace;

namespace Radios
{
    /// <summary>Where a running countdown sends its sound and its words.</summary>
    public interface ICountdownOutput
    {
        /// <summary>Play one beat's tone. Never called for a beat that is not beeped.</summary>
        void Tone(CountdownBeat beat);

        /// <summary>Queue one piece of the count for speech: the lead-in, or a number.</summary>
        void Say(string text);

        /// <summary>
        /// True while a piece already given to <see cref="Say"/> has not yet
        /// been started by the reader. The count waits on this before every
        /// beat and before the key (Codex round 1, blocker 1).
        /// </summary>
        bool SpeechOwed { get; }

        /// <summary>
        /// Take back every piece not yet said. Called when the count is called
        /// off, and when its words could not be delivered (blocker 2).
        /// </summary>
        void WithdrawSpeech(string why);
    }

    /// <summary>
    /// A countdown playing while a check is set up, and the gate that says when
    /// the check may key (#835, Noel 2026-10-28 21:35).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule, ruled for the off-air take and Loopback:</b> <i>"you set up
    /// the take as you're counting down. If the countdown is too short, delay
    /// it a bit."</i> So the setup runs DURING the count, and the key goes down
    /// only when BOTH the count has ended and the setup is confirmed. If the
    /// setup is slower, the LAST beat waits for it: the long tone is the
    /// signal that the radio is about to key, so it never sounds before the
    /// radio is ready to.
    /// </para>
    /// <para>
    /// <b>How the two meet.</b> The start path calls <see cref="Start"/> as it
    /// begins setting up, and <see cref="ReadyToKeyAsync"/> at the one place it
    /// would otherwise key. Reaching that call IS the setup being confirmed;
    /// the call then waits out whatever of the count remains. A start that
    /// fails or is cancelled calls <see cref="Cancel"/>, and the count stops on
    /// the beat it had reached, before its long tone.
    /// </para>
    /// <para>
    /// <b>It errs late, never early.</b> Every wait is measured from the
    /// countdown's own start on its own clock, so a busy thread delays a beat
    /// rather than pulling the next one forward, and a late setup moves every
    /// beat after it, including the end.
    /// </para>
    /// <para>
    /// <b>And it follows its words, not only its clock (Codex round 1).</b> A
    /// beat does not fall while a piece of the count already given to the
    /// reader is still unsaid, and the count does not end, so nothing keys,
    /// until its last number has been said. A busy reader therefore slows the
    /// count instead of leaving its numbers behind; with sounds off those
    /// numbers are the only warning. If the words are not delivered within
    /// <see cref="SpeechCeilingMs"/>, a count that speaks its numbers stops and
    /// does not key (<see cref="SpeechStalled"/>); a beeped count, whose pips
    /// are the warning, drops its unsaid lead-in and counts on. Either way the
    /// unsaid words are taken back, as they are when the count is called off.
    /// </para>
    /// </remarks>
    public sealed class CountdownRun
    {
        private readonly CountdownPlan _plan;
        private readonly ICountdownOutput _output;
        private readonly string _what;
        private readonly Func<long> _nowMs;
        private readonly Func<int, CancellationToken, Task> _delay;
        private readonly CancellationTokenSource _cancel = new();
        private readonly TaskCompletionSource<bool> _arranged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<bool> _completion = Task.FromResult(false);
        private volatile bool _ended;

        /// <summary>The plan this countdown is playing.</summary>
        public CountdownPlan Plan => _plan;

        /// <summary>True from <see cref="Start"/> until the count has ended or been cancelled.</summary>
        public bool IsRunning => !_ended;

        /// <summary>True once <see cref="Cancel"/> has been called.</summary>
        public bool IsCancelled => _cancel.IsCancellationRequested;

        /// <summary>How long the long tone was held back waiting for the setup, in milliseconds. Zero when the setup kept up.</summary>
        public long SetupWaitMs { get; private set; }

        /// <summary>True once the countdown's long tone has sounded (or, unvoiced, been reached).</summary>
        public bool LandingReached { get; private set; }

        /// <summary>True when the count stopped because its words were not delivered in time; nothing keys.</summary>
        public bool SpeechStalled { get; private set; }

        /// <summary>How long a piece of the count may stay unsaid before the count gives up on it.</summary>
        public const int SpeechCeilingMs = 8000;

        /// <summary>How often an unsaid piece is asked about while the count waits for it.</summary>
        public const int SpeechPollMs = 25;

        /// <summary>Ends true when the count has run out with the setup confirmed; false when it was cancelled.</summary>
        public Task<bool> Completion => _completion;

        private CountdownRun(CountdownPlan plan, ICountdownOutput output, string what,
            Func<long>? nowMs, Func<int, CancellationToken, Task>? delay)
        {
            _plan = plan ?? throw new ArgumentNullException(nameof(plan));
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _what = what ?? "";
            if (nowMs == null)
            {
                var clock = Stopwatch.StartNew();
                nowMs = () => clock.ElapsedMilliseconds;
            }
            _nowMs = nowMs;
            _delay = delay ?? ((ms, token) => Task.Delay(ms, token));
        }

        /// <summary>Start a countdown. It plays at once; the caller sets up while it plays.</summary>
        /// <param name="plan">The countdown to play.</param>
        /// <param name="output">Its sound and speech.</param>
        /// <param name="what">What is counting down, for the trace.</param>
        /// <param name="nowMs">A clock in milliseconds; a stopwatch when null. Tests pass a virtual one.</param>
        /// <param name="delay">A delay; <c>Task.Delay</c> when null. Tests pass one that moves the virtual clock.</param>
        public static CountdownRun Start(CountdownPlan plan, ICountdownOutput output, string what,
            Func<long>? nowMs = null, Func<int, CancellationToken, Task>? delay = null)
        {
            var run = new CountdownRun(plan, output, what, nowMs, delay);
            Tracing.TraceLine("Countdown: started for " + run._what + ", " + plan.Beats.Count
                + " beats, " + plan.Voicing + ", lead-in " + (plan.LeadIn == null ? "off" : "on")
                + ", the key may go down at " + plan.EndsAtMs + " ms", TraceLevel.Info);
            run._completion = run.RunAsync();
            return run;
        }

        /// <summary>
        /// The setup is confirmed: wait out the rest of the count. True means
        /// the check may key now; false means the count was cancelled and it
        /// must not.
        /// </summary>
        public Task<bool> ReadyToKeyAsync()
        {
            _arranged.TrySetResult(true);
            return _completion;
        }

        /// <summary>
        /// Stop the count where it is. Safe to call at any time and more than
        /// once; after the count has ended it changes nothing.
        /// </summary>
        public void Cancel()
        {
            // After the count has ended the check has keyed on it; cancelling
            // then would only silence a "1" the screen reader has not reached.
            if (_ended) return;
            _arranged.TrySetResult(false);
            try { _cancel.Cancel(); } catch (ObjectDisposedException) { }
            // Its words go too (Codex round 1, blocker 2): a number still in
            // the queue would otherwise be spoken after the check was called
            // off, or into the next start.
            Withdraw("the countdown being called off");
        }

        private async Task<bool> RunAsync()
        {
            bool ok = false;
            try
            {
                long origin = _nowMs();
                long shift = 0;

                if (_plan.LeadIn != null) Say(_plan.LeadIn);

                foreach (var beat in _plan.Beats)
                {
                    await WaitUntil(origin + shift + beat.AtMs);
                    // The piece before this beat must have been said first; a
                    // late one moves this beat and every beat after it.
                    if (!await WaitForWords()) return false;
                    long behind = _nowMs() - (origin + shift + beat.AtMs);
                    if (behind > 0) shift += behind;

                    if (beat.IsLanding)
                    {
                        // The long tone waits for the setup: it means the radio
                        // is about to key, and it must not say so early.
                        bool arranged = await _arranged.Task.WaitAsync(_cancel.Token);
                        if (!arranged || _cancel.IsCancellationRequested) return false;
                        long late = _nowMs() - (origin + shift + beat.AtMs);
                        if (late > 0)
                        {
                            shift += late;
                            SetupWaitMs = late;
                            Tracing.TraceLine("Countdown: " + _what + " held its last beat " + late
                                + " ms for the setup to be confirmed", TraceLevel.Info);
                        }
                        LandingReached = true;
                    }

                    if (_cancel.IsCancellationRequested) return false;
                    if (beat.Beeped) Tone(beat);
                    if (beat.Spoken != null) Say(beat.Spoken);
                }

                await WaitUntil(origin + shift + _plan.EndsAtMs);
                // The last number must have been said before anything keys;
                // if it was late, it still gets its beat.
                long sayingFrom = _nowMs();
                if (!await WaitForWords()) return false;
                if (_nowMs() > sayingFrom) await WaitUntil(_nowMs() + CountdownSettings.BeatMs);
                ok = !_cancel.IsCancellationRequested;
                return ok;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                // A countdown that faults must never let a check key early, and
                // must never take the check down with it: it ends, false, and
                // the start path rolls back as it would for a cancel.
                Tracing.TraceLine("Countdown: " + _what + " faulted and ended without keying: " + ex.Message,
                    TraceLevel.Warning);
                return false;
            }
            finally
            {
                _ended = true;
                Tracing.TraceLine(ok
                    ? "Countdown: " + _what + " ended; the key may go down"
                    : "Countdown: " + _what + " stopped before it ended; nothing keys from it",
                    TraceLevel.Info);
            }
        }

        /// <summary>
        /// Wait until every piece of the count given to the reader has been
        /// started. True to carry on; false when the words could not be
        /// delivered and the count must not key.
        /// </summary>
        private async Task<bool> WaitForWords()
        {
            long from = _nowMs();
            while (Owed())
            {
                if (_nowMs() - from >= SpeechCeilingMs)
                {
                    Withdraw("the countdown's words not being delivered");
                    if (_plan.Voicing == CountdownVoicing.Beeped)
                    {
                        // Only the lead-in is spoken in a beeped count, and the
                        // pips are its warning: drop the words, keep counting.
                        Tracing.TraceLine("Countdown: " + _what + " dropped its lead-in, unsaid after "
                            + SpeechCeilingMs + " ms; the pips count on", TraceLevel.Warning);
                        return true;
                    }
                    SpeechStalled = true;
                    Tracing.TraceLine("Countdown: " + _what + " stopped: its words were not said within "
                        + SpeechCeilingMs + " ms, so nothing keys", TraceLevel.Warning);
                    return false;
                }
                await _delay(SpeechPollMs, _cancel.Token);
                _cancel.Token.ThrowIfCancellationRequested();
            }
            return true;
        }

        /// <summary>
        /// Whether words are still owed. A speech layer that throws reads as
        /// OWED: with sounds off the words are the only warning, so a count
        /// that cannot tell must wait, and in the end stop, rather than key.
        /// </summary>
        private bool Owed()
        {
            try { return _output.SpeechOwed; }
            catch (Exception ex)
            {
                Tracing.TraceLine("Countdown: asking whether its words were said threw: " + ex.Message,
                    TraceLevel.Warning);
                return true;
            }
        }

        private void Withdraw(string why)
        {
            try { _output.WithdrawSpeech(why); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Countdown: taking its words back threw: " + ex.Message, TraceLevel.Warning);
            }
        }

        private async Task WaitUntil(long at)
        {
            long wait = at - _nowMs();
            if (wait > 0) await _delay((int)Math.Min(wait, int.MaxValue), _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
        }

        private void Tone(CountdownBeat beat)
        {
            try { _output.Tone(beat); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Countdown: a tone threw and was ignored: " + ex.Message, TraceLevel.Warning);
            }
        }

        private void Say(string text)
        {
            try { _output.Say(text); }
            catch (Exception ex)
            {
                Tracing.TraceLine("Countdown: speech threw and was ignored: " + ex.Message, TraceLevel.Warning);
            }
        }
    }
}
