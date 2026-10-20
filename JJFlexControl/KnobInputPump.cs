using System;
using System.Diagnostics;
using System.Text;

namespace JJFlexControl
{
    /// <summary>
    /// One open port's worth of knob input (#520): the text the port delivers, split into tokens,
    /// passed through <see cref="KnobInputFilter"/>'s wait for quiet, and dispatched in order. One
    /// lock orders reads, buffer checks, dispatch and <see cref="Stop"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Nothing is held</b>, so a close has nothing to release: every input code after the
    /// knob has been found quiet is dispatched in the call that read it.</para>
    /// <para><b>The port is READ inside the lock, and its buffer CHECKED inside the same lock</b>
    /// (round five). That is what makes a buffer check mean something: text taken out of the port
    /// but not yet handed to the filter would otherwise be invisible to a check made in between,
    /// and the check would find an empty buffer while saved turns sat in a thread's hands.</para>
    /// <para><b>Nothing is dispatched after close.</b> <see cref="Stop"/> takes the same lock as
    /// every dispatch, so it waits for one already in progress, and every later read or check sees
    /// the stopped flag inside the lock.</para>
    /// <para><b>The parse is inside the lock too, and the buffer belongs to this open.</b> A late
    /// callback from a port that has been closed and reopened cannot touch the new open's buffer,
    /// because each open has its own pump.</para>
    /// <para>A fresh pump, so a fresh wait for quiet, at EVERY open: Noel's buffer ruling applies
    /// to each one.</para>
    /// <para><b>Read times are not byte times.</b> Everything in one read is stamped with one
    /// reading of the clock, taken inside the lock; that is when the text was read from the port,
    /// not when its bytes arrived.</para>
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

        /// <summary>True until a buffer check has found the knob quiet.</summary>
        public bool WaitingForQuiet
        {
            get { lock (_gate) return _filter.WaitingForQuiet; }
        }

        /// <summary>
        /// Read whatever the port has, inside the pump's lock, and act on it. <paramref name="read"/>
        /// returns the text exactly as the port delivered it (null or empty for nothing).
        /// </summary>
        public void ReadFrom(Func<string> read)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            lock (_gate)
            {
                if (_stopped) return;
                Take(read(), _clockMs());
            }
        }

        /// <summary>
        /// Text exactly as the port delivered it: semicolon-delimited codes, nulls stripped,
        /// empty tokens ignored. Everything in one call was read together and shares one time.
        /// </summary>
        public void TakeText(string text) => ReadFrom(() => text);

        /// <summary>
        /// Check the port's buffer while waiting for quiet. <paramref name="unreadBytes"/> is read
        /// inside the pump's lock, so no read is half done. Returns true while still waiting, so
        /// the caller knows when to stop checking.
        /// </summary>
        public bool CheckQuiet(Func<int> unreadBytes)
        {
            if (unreadBytes == null) throw new ArgumentNullException(nameof(unreadBytes));
            lock (_gate)
            {
                if (_stopped || !_filter.WaitingForQuiet) return false;
                _filter.CheckQuiet(_clockMs(), unreadBytes());
                return _filter.WaitingForQuiet;
            }
        }

        private void Take(string text, double now)
        {
            if (string.IsNullOrEmpty(text)) return;
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
