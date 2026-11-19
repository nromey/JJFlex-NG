using System;
using System.Threading;

namespace Radios
{
    /// <summary>
    /// What the remote-audio loop waits on when it has nothing to do (#1156).
    /// One per PC-audio run, wrapping one <see cref="AutoResetEvent"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> The loop used to call <c>Thread.Yield()</c> when no
    /// receive packet was waiting, at Highest priority. <c>Yield</c> returns at
    /// once when nothing else is ready on that core, so the thread spun: the
    /// 2026-11-18 bench measured it at 98.6 to 99.4 percent of a core whenever
    /// PC audio was on, muted or not.
    /// </para>
    /// <para>
    /// <b>Who signals it.</b> FlexLib's stock <c>RXAudioStream.OpusPacketReceived</c>
    /// on its network thread (a packet arrived), <c>noteRadioMox</c> on a key
    /// edge, <c>stopRemoteAudioThread</c>, the FlexBase tone and file engage and
    /// release, and a PortAudio stream that finished on its own (#1145). Anything
    /// else the loop polls for is noticed within one
    /// <see cref="IdleWakeMilliseconds"/>.
    /// </para>
    /// <para>
    /// <b>It cannot lose a wakeup.</b> The loop waits only after finding the
    /// packet list empty. A signal set between that check and the wait leaves
    /// the event set, so the wait returns at once. Several signals while the
    /// loop is busy collapse into one wake, which is harmless: the loop drains
    /// one packet per pass and does not wait while packets remain.
    /// </para>
    /// <para>
    /// <b>No signaller may ever be hurt by it.</b> Four threads signal it,
    /// including FlexLib's receive thread and PortAudio's native thread, where
    /// an exception is fatal. A signal that races disposal is swallowed, and a
    /// wait on a disposed wake returns at once.
    /// </para>
    /// </remarks>
    internal sealed class RemoteAudioWake : IDisposable
    {
        /// <summary>
        /// How long the loop waits when no packet is waiting and nothing has
        /// signalled. It bounds how late the loop notices a state it polls for
        /// rather than being told about, which today is a generated transmit
        /// source finishing its release ramp. Packet arrival and key edges wake
        /// the loop directly, so this is not added to receive or keying latency.
        /// </summary>
        /// <remarks>
        /// Five milliseconds is the design's starting judgement, not a
        /// measurement. While a WASAPI stream is open PortAudio raises this
        /// process's timer resolution to 1 ms, so the wait lasts about 5 ms;
        /// otherwise it can last up to about 15.6 ms.
        /// </remarks>
        public const int IdleWakeMilliseconds = 5;

        /// <summary>
        /// How long the loop idles per pass while the connection is being torn
        /// down. The same ten milliseconds as the <c>Thread.Sleep(10)</c> it
        /// replaced, except that a stop request now ends it early.
        /// </summary>
        public const int DisconnectingWaitMilliseconds = 10;

        private readonly AutoResetEvent _event = new AutoResetEvent(false);
        private int _disposed;

        /// <summary>Wake the loop. Safe from any thread, at any time, including after disposal.</summary>
        public void Signal()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _event.Set(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Wait until signalled or until <paramref name="timeoutMs"/> passes.
        /// True when a signal ended the wait. A disposed wake returns true at
        /// once, so a loop that outlived its wake cannot block on it.
        /// </summary>
        public bool WaitForWork(int timeoutMs)
        {
            if (Volatile.Read(ref _disposed) != 0) return true;
            try { return _event.WaitOne(timeoutMs); }
            catch (ObjectDisposedException) { return true; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _event.Dispose();
        }
    }
}
