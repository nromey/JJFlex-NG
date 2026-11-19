using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, an automatic tune's start revoked by a stop (batch 14 repair 11).
    /// </summary>
    /// <remarks>
    /// <para><b>Why.</b> In automatic type a start queues two writes, our key
    /// (Transmit = true) and the radio's "atu start", and sets the latch. A stop
    /// accepted before the command worker had run them cleared the latch and
    /// queued our unkey, but the queued key and "atu start" still went out
    /// after it: FlexLib sends "atu start" unconditionally. Codex's B1 on
    /// db03106b, from the source; not seen on a radio.</para>
    /// <para><b>The rule.</b> Every change of the automatic latch takes a new
    /// generation, and a start's two writes go out only while their generation
    /// is still the current one, checked under the same lock a stop takes to
    /// move it. So a stop either comes first and the start is never written, or
    /// comes after the write and its own queued unkey follows. The same shape
    /// as the plain carrier's write generation (FlexBase.TuneAnswer.cs).</para>
    /// </remarks>
    public partial class FlexBase
    {
        private readonly object _atuStartGate = new object();
        private long _atuStartGen;
        /// <summary>The generation whose key-on our automatic start queued, or -1.</summary>
        private long _atuKeyQueuedFor = -1;

        /// <summary>
        /// A new automatic-latch generation: revokes every start not yet written.
        /// When the previous generation's key-on was already queued (the start's
        /// first write ran) and has not been written, the stop also revokes it,
        /// through the queue's own revocation of admitted transmit work, so no
        /// key goes out after the stop. Unkeys are never revoked.
        /// </summary>
        private long NextAtuStartGeneration()
        {
            bool revokeKey;
            long now;
            lock (_atuStartGate)
            {
                long prev = _atuStartGen;
                revokeKey = _atuKeyQueuedFor == prev && !_ownKeyRequested;
                now = ++_atuStartGen;
            }
            if (revokeKey) RevokeAdmittedTransmitWork("an automatic tune was stopped before its key went out");
            return now;
        }

        /// <summary>
        /// Run one of an automatic start's writes only if no later change of the
        /// latch has revoked it, holding the gate across the write.
        /// </summary>
        private void WriteAtuStartIfCurrent(long generation, string what, System.Action write)
        {
            lock (_atuStartGate)
            {
                if (generation != _atuStartGen)
                {
                    Tracing.TraceLine("FlexTunerOn: " + what + " not sent; a stop revoked this automatic start", TraceLevel.Info);
                    return;
                }
                write();
            }
        }
    }
}
