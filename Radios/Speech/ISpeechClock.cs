#nullable enable
using System;

namespace Radios.Speech
{
    /// <summary>
    /// The speech arbiter's view of time — a readable clock and one-shot
    /// timers, and nothing else.
    ///
    /// This seam exists because the coalescer's behaviour IS its timing.
    /// Lead-then-settle, the sweep window, the minimum gap between our own
    /// utterances: every one of them is a claim about milliseconds, and with
    /// wall-clock time (<c>DateTime.UtcNow</c> plus
    /// <c>System.Threading.Timer</c>) the only way to test those claims was to
    /// sleep and hope — slow, flaky, and exactly the "instrument that lies
    /// occasionally" this codebase keeps paying to remove. With the clock
    /// injected, a test advances time by hand and asserts precisely one
    /// utterance, at precisely the tick the constant names.
    ///
    /// Production uses <see cref="SystemSpeechClock"/>; tests supply a manual
    /// clock whose timers fire only when the test says time has passed.
    /// </summary>
    internal interface ISpeechClock
    {
        /// <summary>The current instant. Monotonic enough for spacing decisions.</summary>
        DateTime UtcNow { get; }

        /// <summary>
        /// Start a one-shot timer that invokes <paramref name="callback"/>
        /// after <paramref name="dueMs"/> milliseconds. Re-arm with
        /// <see cref="ISpeechTimer.Change"/>; a disposed timer's Change throws
        /// <see cref="ObjectDisposedException"/>, matching
        /// <c>System.Threading.Timer</c>, because the arbiter's race handling
        /// keys on exactly that exception.
        /// </summary>
        ISpeechTimer StartTimer(int dueMs, Action callback);
    }

    /// <summary>A one-shot timer handle. See <see cref="ISpeechClock.StartTimer"/>.</summary>
    internal interface ISpeechTimer : IDisposable
    {
        /// <summary>Re-arm to fire once, <paramref name="dueMs"/> ms from now.</summary>
        void Change(int dueMs);
    }

    /// <summary>Wall-clock implementation used in production.</summary>
    internal sealed class SystemSpeechClock : ISpeechClock
    {
        public DateTime UtcNow => DateTime.UtcNow;

        public ISpeechTimer StartTimer(int dueMs, Action callback) =>
            new SystemSpeechTimer(dueMs, callback);

        private sealed class SystemSpeechTimer : ISpeechTimer
        {
            private readonly System.Threading.Timer _timer;

            public SystemSpeechTimer(int dueMs, Action callback)
            {
                _timer = new System.Threading.Timer(
                    _ => callback(), null, dueMs, System.Threading.Timeout.Infinite);
            }

            public void Change(int dueMs) =>
                _timer.Change(dueMs, System.Threading.Timeout.Infinite);

            public void Dispose() => _timer.Dispose();
        }
    }
}
