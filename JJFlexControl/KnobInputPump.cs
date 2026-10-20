using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace JJFlexControl
{
    /// <summary>
    /// One open port's worth of knob input (#520): the text the port delivers, split into tokens,
    /// run through <see cref="KnobInputFilter"/>, and dispatched in order. It owns the filter's
    /// release timer and the one lock that orders dispatch between the port's thread, the timer's
    /// thread and <see cref="Stop"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Close loses no real step.</b> <see cref="Stop"/> takes the same lock as every
    /// dispatch, so it waits for one already in progress, then decides what is held as if nothing
    /// more will arrive: a real step taken just before close is acted on, a burst is still
    /// discarded. After <see cref="Stop"/> returns, nothing this pump holds is ever dispatched.</para>
    /// <para><b>The parse is inside the lock too, and the buffer belongs to this open.</b> A late
    /// callback from a port that has been closed and reopened cannot touch the new open's buffer,
    /// because each open has its own pump.</para>
    /// <para>A fresh pump, so a fresh filter, at EVERY open: Noel's buffer ruling applies to each
    /// one.</para>
    /// </remarks>
    public sealed class KnobInputPump
    {
        private const char Delimiter = ';';

        private readonly object _gate = new object();
        private readonly Action<string> _dispatch;
        private readonly Func<double> _clockMs;
        private readonly Action<long> _schedule;
        private readonly Timer _timer;
        private readonly Action<string> _note;
        private readonly KnobInputFilter _filter;
        private readonly StringBuilder _pending = new StringBuilder();
        private bool _stopped;

        /// <param name="dispatch">Acts on one token. Called under the pump's lock, in order.</param>
        /// <param name="clockMs">Milliseconds on any monotonic clock; a Stopwatch when null.</param>
        /// <param name="schedule">Told how many milliseconds until <see cref="ReleaseDue"/> should
        /// run, or <see cref="Timeout.Infinite"/>. When null the pump runs its own timer.</param>
        /// <param name="note">Trace sink for discards and device reports. May be null.</param>
        public KnobInputPump(Action<string> dispatch, Func<double> clockMs = null,
            Action<long> schedule = null, Action<string> note = null)
        {
            _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            _note = note;
            _filter = new KnobInputFilter(note);
            if (clockMs == null)
            {
                Stopwatch watch = Stopwatch.StartNew();
                clockMs = () => watch.Elapsed.TotalMilliseconds;
            }
            _clockMs = clockMs;
            if (schedule == null)
            {
                _timer = new Timer(_ => ReleaseDue(), null, Timeout.Infinite, Timeout.Infinite);
                schedule = wait =>
                {
                    try { _timer.Change(wait, Timeout.Infinite); }
                    catch (ObjectDisposedException) { }
                };
            }
            _schedule = schedule;
        }

        /// <summary>Bursts discarded so far, for the trace and the tests.</summary>
        public int BurstEventsDiscarded
        {
            get { lock (_gate) return _filter.BurstEventsDiscarded; }
        }

        /// <summary>
        /// Text exactly as the port delivered it: semicolon-delimited codes, nulls stripped,
        /// empty tokens ignored. Everything in one call arrived together and shares one time.
        /// </summary>
        public void TakeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_gate)
            {
                if (_stopped) return;
                double now = _clockMs(); // stamped inside the lock: the timer cannot have decided later
                foreach (char ch in text)
                {
                    if (ch == '\0') continue;
                    if (ch != Delimiter)
                    {
                        _pending.Append(ch);
                        continue;
                    }
                    if (_pending.Length == 0) continue;
                    string token = _pending.ToString();
                    _pending.Clear();
                    if (KnobInputFilter.IsDeviceReport(token)) _note?.Invoke("device report kept:" + token);
                    DispatchAll(_filter.Accept(token, now));
                }
                Rearm(now);
            }
        }

        /// <summary>The release timer's work: act on whatever the filter has now decided.</summary>
        public void ReleaseDue()
        {
            lock (_gate)
            {
                if (_stopped) return;
                double now = _clockMs();
                DispatchAll(_filter.Release(now));
                Rearm(now);
            }
        }

        /// <summary>
        /// The port is closing. Waits for any dispatch in progress, decides what is held, dispatches
        /// the real steps among it, and guarantees nothing further is dispatched.
        /// </summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_stopped) return;
                _stopped = true;
                DispatchAll(_filter.Flush());
                _schedule(Timeout.Infinite);
            }
            _timer?.Dispose();
        }

        private void Rearm(double now)
        {
            double? due = _filter.NextDueMs;
            _schedule(due == null ? Timeout.Infinite : Math.Max(1L, (long)Math.Ceiling(due.Value - now)));
        }

        private void DispatchAll(IReadOnlyList<string> tokens)
        {
            foreach (string token in tokens) _dispatch(token);
        }
    }
}
