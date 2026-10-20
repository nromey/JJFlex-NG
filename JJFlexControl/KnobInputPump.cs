using System;
using System.Diagnostics;
using System.Text;

namespace JJFlexControl
{
    /// <summary>
    /// One open port's worth of knob input (#520): the text the port delivers, split into tokens,
    /// passed through <see cref="KnobInputFilter"/>'s wait for quiet, and dispatched in order. One
    /// lock orders dispatch between the port's thread and <see cref="Stop"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Nothing is held</b>, so a close has nothing to release: every input code after the
    /// knob has gone quiet is dispatched in the call that read it.</para>
    /// <para><b>Nothing is dispatched after close.</b> <see cref="Stop"/> takes the same lock as
    /// every dispatch, so it waits for one already in progress, and every later read sees the
    /// stopped flag inside the lock.</para>
    /// <para><b>The parse is inside the lock too, and the buffer belongs to this open.</b> A late
    /// callback from a port that has been closed and reopened cannot touch the new open's buffer,
    /// because each open has its own pump.</para>
    /// <para>A fresh pump, so a fresh wait for quiet, at EVERY open: Noel's buffer ruling applies
    /// to each one.</para>
    /// <para><b>Arrival times are callback times.</b> Everything in one <see cref="TakeText"/>
    /// call is stamped with one reading of the clock, taken when the call gets the lock; that is
    /// when Windows handed the text over, not when its bytes arrived.</para>
    /// </remarks>
    public sealed class KnobInputPump
    {
        private const char Delimiter = ';';

        private readonly object _gate = new object();
        private readonly Action<string> _dispatch;
        private readonly Func<double> _clockMs;
        private readonly Action<string> _note;
        private readonly KnobInputFilter _filter;
        private readonly StringBuilder _pending = new StringBuilder();
        private bool _stopped;

        /// <param name="dispatch">Acts on one token. Called under the pump's lock, in order.</param>
        /// <param name="clockMs">Milliseconds on any monotonic clock; a Stopwatch when null. The
        /// wait for quiet counts from its reading when the pump is made, so make the pump just
        /// before the port is asked to open: nothing can arrive earlier than that.</param>
        /// <param name="note">Trace sink for the wait's summary and for device reports. May be null.</param>
        public KnobInputPump(Action<string> dispatch, Func<double> clockMs = null, Action<string> note = null)
        {
            _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            _note = note;
            if (clockMs == null)
            {
                Stopwatch watch = Stopwatch.StartNew();
                clockMs = () => watch.Elapsed.TotalMilliseconds;
            }
            _clockMs = clockMs;
            _filter = new KnobInputFilter(_clockMs(), note);
        }

        /// <summary>Input codes ignored while waiting for quiet, for the trace and the tests.</summary>
        public int ReplayEventsDiscarded
        {
            get { lock (_gate) return _filter.ReplayEventsDiscarded; }
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
                double now = _clockMs();
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
                    if (_filter.Accept(token, now)) _dispatch(token);
                }
            }
        }

        /// <summary>
        /// The port is closing. Waits for any dispatch in progress and guarantees nothing further
        /// is dispatched.
        /// </summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_stopped) return;
                _stopped = true;
                _filter.Close(_clockMs());
            }
        }
    }
}
