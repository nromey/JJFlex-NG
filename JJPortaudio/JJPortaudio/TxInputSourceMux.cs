using System;

namespace JJPortaudio
{
    /// <summary>
    /// Lets several things share the one transmit injection slot
    /// (Sprint 33 Track I).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The PortAudio input callback has exactly one place where samples may
    /// replace the microphone, and that is correct — two sources both
    /// overwriting the same buffer in the same callback would produce
    /// whichever one ran last, silently. So the arbitration has to happen
    /// somewhere, and it happens here rather than in the callback, where it
    /// would have to be edited again for every new source.
    /// </para>
    /// <para>
    /// The rule is priority order, first engaged wins, and it is deliberately
    /// blunt: sources are listed once at wiring time and the earliest engaged
    /// one in the list owns the buffer. Nothing is mixed, nothing is queued,
    /// and a second source engaging while a first is running does not
    /// interrupt it — the surfaces that start these things refuse out loud
    /// when something else is already transmitting, which is a better place
    /// for that conversation than an audio callback.
    /// </para>
    /// <para>
    /// The list is fixed after construction, so the audio thread reads an
    /// array that never changes shape.
    /// </para>
    /// </remarks>
    public sealed class TxInputSourceMux : ITxInputSource
    {
        private readonly ITxInputSource[] _sources;

        /// <summary>
        /// Wire the sources, highest priority first.
        /// </summary>
        public TxInputSourceMux(params ITxInputSource[] sources)
        {
            _sources = sources ?? Array.Empty<ITxInputSource>();
        }

        /// <summary>The engaged source, or null when the mic passes untouched.</summary>
        public ITxInputSource Active
        {
            get
            {
                var sources = _sources;
                for (int i = 0; i < sources.Length; i++)
                {
                    var s = sources[i];
                    if (s != null && s.Engaged) return s;
                }
                return null;
            }
        }

        /// <inheritdoc/>
        public bool Engaged => Active != null;

        /// <inheritdoc/>
        /// <remarks>
        /// Answers for whichever source is engaged, so the callback's decision
        /// to stand the conditioning chain down follows the thing that is
        /// actually producing samples. Nothing engaged means nothing to
        /// bypass.
        /// </remarks>
        public bool BypassesConditioning
        {
            get
            {
                var s = Active;
                return s != null && s.BypassesConditioning;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Every source is called, engaged or not. That is not a shortcut
        /// worth taking: an idle source still has to see the buffer to stamp
        /// its stream-gap clock and to run its release ramp back to the
        /// microphone. Skipping the idle ones would hard-cut the mic at every
        /// release — the exact click these ramps exist to prevent.
        ///
        /// <para>
        /// Called in REVERSE list order so that the highest-priority source
        /// writes last and its samples are the ones that survive. Calling them
        /// forwards would let the lowest-priority source overwrite the winner,
        /// which is the opposite of what <see cref="Active"/> reports — and a
        /// mux whose answer disagrees with its own output is worse than no mux.
        /// </para>
        /// </remarks>
        public void Process(float[] buffer, int count, uint sampleRate)
        {
            var sources = _sources;
            for (int i = sources.Length - 1; i >= 0; i--)
            {
                sources[i]?.Process(buffer, count, sampleRate);
            }
        }
    }
}
