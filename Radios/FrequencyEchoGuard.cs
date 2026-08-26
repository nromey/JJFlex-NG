using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Rejects the one stale echo that would rewind a frequency we have just
    /// written, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bug (#266).</b> Tuning is a read-modify-write against a cache the
    /// radio also writes. A press reads <c>_RXFrequency</c>, adds a step and
    /// writes it back; the setter updates the cache immediately and queues the
    /// real write to the radio. Meanwhile an in-flight <c>Freq</c> notification —
    /// carrying the value from BEFORE our write, because the radio had not seen
    /// it yet — lands in the slice handler and puts the old number back. The next
    /// press reads that old number and steps from there, so the dial
    /// under-travels; and because the ordering of echoes against presses varies
    /// with latency, it also goes non-monotonic.
    /// </para>
    /// <para>
    /// <b>Why it is a remote-only symptom.</b> On a LAN the round trip is short
    /// enough that the window barely exists. Over SmartLink it is tens to
    /// hundreds of milliseconds wide, which is longer than an operator holding a
    /// tuning key takes to press again.
    /// </para>
    /// <para>
    /// <b>What this rejects, precisely.</b> Only an echo equal to the value our
    /// write replaced. That is a deliberately narrow rule and the narrowness is
    /// the point: a clamp at a band edge, another MultiFlex client tuning, or the
    /// operator turning the physical knob all report a value we did not replace,
    /// so they are accepted at once. A guard that swallowed every unexpected echo
    /// would make the radio stop being the authority, which is a worse bug than
    /// the one being fixed and a far quieter one.
    /// </para>
    /// <para>
    /// <b>It always gives up.</b> If our write is dropped, the radio keeps
    /// reporting the old value truthfully and there is no confirmation coming.
    /// After <see cref="GiveUpMs"/> the guard accepts whatever the radio says and
    /// traces that it did. Holding a wrong cache forever — telling a blind
    /// operator they are on a frequency they are not on — is not a trade worth
    /// making for a race window.
    /// </para>
    /// </remarks>
    internal sealed class FrequencyEchoGuard
    {
        /// <summary>
        /// How long to wait for our own write to be confirmed before deciding it
        /// never arrived and letting the radio win.
        /// </summary>
        /// <remarks>
        /// Sized against the thing being defended: a slow SmartLink round trip is
        /// commonly 100-300 ms, so this is roughly four times the worst case. It
        /// also bounds the damage of a dropped write, because until it expires we
        /// are reporting a frequency the radio is not on. Both pressures point
        /// the same way and this is the compromise.
        /// </remarks>
        internal const int GiveUpMs = 1200;

        private readonly object _lock = new object();
        private readonly string _name;
        private readonly Func<long> _clock;

        private bool _armed;
        private ulong _replaced;
        private ulong _requested;
        private long _armedAt;

        /// <param name="name">Appears in traces. "RX" or "TX".</param>
        /// <param name="clock">
        /// Milliseconds from any fixed origin. Injectable so the give-up path can
        /// be tested without waiting for it, which is the only way that path ever
        /// gets exercised deliberately.
        /// </param>
        internal FrequencyEchoGuard(string name, Func<long> clock = null)
        {
            _name = name;
            _clock = clock ?? (() => Environment.TickCount64);
        }

        /// <summary>
        /// Record that we have just written <paramref name="requested"/> over
        /// <paramref name="replaced"/>. Call this only where the cache is updated
        /// ahead of the radio; a path that lets the echo be the truth must not
        /// arm, or it would reject its own answer.
        /// </summary>
        internal void Requested(ulong replaced, ulong requested)
        {
            lock (_lock)
            {
                _armed = true;
                _replaced = replaced;
                _requested = requested;
                _armedAt = _clock();
            }
        }

        /// <summary>
        /// Should <paramref name="echoed"/> be written into the cache? True for
        /// everything except a stale report of the value we just replaced.
        /// </summary>
        internal bool Accept(ulong echoed)
        {
            lock (_lock)
            {
                if (!_armed) return true;

                if (echoed == _requested)
                {
                    // Settled. The radio has our value; there is nothing left to
                    // protect.
                    _armed = false;
                    return true;
                }

                long age = _clock() - _armedAt;
                if (age >= GiveUpMs)
                {
                    _armed = false;
                    Tracing.TraceLine(
                        "FrequencyEcho:" + _name + " gave up after " + age + " ms waiting for "
                        + _requested + " to be confirmed; accepting " + echoed
                        + ". The write was probably dropped.", TraceLevel.Warning);
                    return true;
                }

                if (echoed == _replaced)
                {
                    Tracing.TraceLine(
                        "FrequencyEcho:" + _name + " ignored a stale echo of " + echoed
                        + " that would have rewound " + _requested + " (" + age + " ms in)",
                        TraceLevel.Info);
                    return false;
                }

                // Not ours and not stale: a band-edge clamp, another client, or
                // the front-panel knob. The radio is the authority.
                _armed = false;
                return true;
            }
        }
    }
}
