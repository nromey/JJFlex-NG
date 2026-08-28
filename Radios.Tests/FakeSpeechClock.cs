#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Speech;

namespace Radios.Tests
{
    /// <summary>
    /// Virtual time for the speech arbiter: a clock a test advances by hand and
    /// one-shot timers that fire only when it says time has passed.
    ///
    /// <para><b>Why this is shared rather than private (#285).</b> The
    /// coalescer's behaviour IS its timing, so every test of it is a claim about
    /// milliseconds. Against wall time the only way to make such a claim was to
    /// sleep and hope — slow, and dependent on what else the machine was doing,
    /// which makes a timing test the natural candidate to become the next test
    /// that fails only in a full suite and teaches people to re-run rather than
    /// look. This clock was built in Sprint 35 and lived private to
    /// <see cref="SpeechArbiterTests"/>, which left
    /// <see cref="SpeechCoalescerTimingTests"/> sleeping for about twelve
    /// seconds beside it, with its own file header saying it should be ported.
    /// Both files now drive this one clock.</para>
    ///
    /// <para>Advance to one millisecond short of a boundary and assert silence;
    /// advance one more and assert exactly one utterance. That is a claim a
    /// sleep cannot make.</para>
    /// </summary>
    internal sealed class FakeSpeechClock : ISpeechClock
    {
        private DateTime _now = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        private readonly List<FakeTimer> _timers = new();
        private long _seq;

        public DateTime UtcNow => _now;

        /// <summary>Where the clock started, so a test can express instants as "t = 700".</summary>
        public DateTime Epoch { get; } = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Milliseconds elapsed since <see cref="Epoch"/>.</summary>
        public double ElapsedMs => (_now - Epoch).TotalMilliseconds;

        public ISpeechTimer StartTimer(int dueMs, Action callback)
        {
            var t = new FakeTimer(this, callback, _now.AddMilliseconds(dueMs), _seq++);
            _timers.Add(t);
            return t;
        }

        /// <summary>
        /// Move time forward, firing due timers in due order. A callback that
        /// re-arms its own timer (the anti-clip gap deferral does) is honoured
        /// within the same advance when the new due time still falls inside it —
        /// exactly as a real timer would fire.
        /// </summary>
        public void Advance(int ms)
        {
            var target = _now.AddMilliseconds(ms);
            while (true)
            {
                FakeTimer? next = _timers
                    .Where(t => !t.Disposed && t.DueAt.HasValue && t.DueAt.Value <= target)
                    .OrderBy(t => t.DueAt!.Value).ThenBy(t => t.Seq)
                    .FirstOrDefault();
                if (next == null) break;
                if (next.DueAt!.Value > _now) _now = next.DueAt.Value;
                next.DueAt = null; // one-shot: consumed unless re-armed
                next.Callback();
            }
            _now = target;
        }

        private sealed class FakeTimer : ISpeechTimer
        {
            private readonly FakeSpeechClock _clock;
            public Action Callback { get; }
            public DateTime? DueAt { get; set; }
            public long Seq { get; }
            public bool Disposed { get; private set; }

            public FakeTimer(FakeSpeechClock clock, Action callback, DateTime dueAt, long seq)
            {
                _clock = clock;
                Callback = callback;
                DueAt = dueAt;
                Seq = seq;
            }

            public void Change(int dueMs)
            {
                // Matches System.Threading.Timer: a disposed timer's Change
                // throws, and the arbiter's race handling keys on that.
                if (Disposed) throw new ObjectDisposedException(nameof(FakeTimer));
                DueAt = _clock._now.AddMilliseconds(dueMs);
            }

            public void Dispose()
            {
                Disposed = true;
                DueAt = null;
            }
        }
    }
}
