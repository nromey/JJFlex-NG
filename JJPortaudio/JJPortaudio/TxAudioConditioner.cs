using System;

namespace JJPortaudio
{
    /// <summary>
    /// The PC-side transmit conditioning chain (Track I): noise reduction,
    /// then the noise gate, with a monitor tap that can play what the chain
    /// REMOVED. Sits in the PortAudio input callback as the third thing at
    /// the established insertion point — the tone source injects, this
    /// modifies, the LUFS meter observes:
    ///
    ///     mic → tone injection → THIS → LUFS meter → Opus encode
    ///
    /// That order preserves the property the meter was built for: it always
    /// measures what genuinely goes out, conditioned audio and all.
    ///
    /// Stage order inside the chain is NR first, gate second. NR lowers the
    /// floor, which makes the gate threshold easier to derive and less likely
    /// to clip quiet speech; reversed, the gate would be judging a noisy
    /// signal.
    ///
    /// The noise-reduction stage is a pluggable delegate rather than a hard
    /// reference: the engine (NoiseReductionProvider, RNNoise) lives in the
    /// UI assembly with its NAudio dependency, and this class stays
    /// System-only so the numerical harness can link it and prove the
    /// residual arithmetic. Whether NR actually runs is the provider's own
    /// business (its Enabled/Strength/mode logic) — this chain just gives it
    /// the buffer.
    ///
    /// THE RESIDUAL MONITOR is why this is buildable by a blind operator at
    /// all: removed = input − output, played to a monitor. Listening to the
    /// OUTPUT can never reveal over-processing, because the missing parts are
    /// not there to hear; hear your own speech in the RESIDUAL and the chain
    /// is eating your voice. It also proves the pathway is live: processing
    /// that is enabled-but-bypassed and processing that is on-and-gentle both
    /// sound clean on the output, but bypassed produces actual SILENCE in the
    /// residual — not something quiet, nothing.
    ///
    /// Monitor modes: Output (what goes to the radio), Residual (what was
    /// removed), and Split (output in the left ear, residual in the right —
    /// the two-ears trade-off view; note that summing them would simply
    /// reconstruct the input, which is why "both" is a stereo split and not a
    /// mix). The sink receives interleaved stereo and must copy synchronously
    /// — it is called on the PortAudio callback thread.
    ///
    /// Thread model: Process runs on the PortAudio callback thread. Setters
    /// run on UI threads; delegate and enum reads are single-word and read
    /// once per buffer. The only allocation is growing the two scratch
    /// buffers, which happens once per stream shape.
    /// </summary>
    public class TxAudioConditioner
    {
        /// <summary>What the monitor tap carries.</summary>
        public enum MonitorModes
        {
            /// <summary>Tap off; sink never called.</summary>
            Off = 0,
            /// <summary>The processed audio — exactly what goes to the radio.</summary>
            Output = 1,
            /// <summary>What the chain removed: input minus output.</summary>
            Residual = 2,
            /// <summary>Output in the left channel, residual in the right.</summary>
            Split = 3
        }

        /// <summary>The transmit noise gate. Its threshold is set by the app
        /// from the measured noise floor (floor + margin), not by hand.</summary>
        public TxNoiseGate Gate { get; } = new TxNoiseGate();

        /// <summary>
        /// Pluggable noise-reduction stage, invoked in place ahead of the
        /// gate: (buffer, floatCount, sampleRate). Null means no NR stage is
        /// attached. The delegate itself decides whether to touch the buffer.
        /// </summary>
        public Action<float[], int, uint> NoiseReducer { get; set; }

        /// <summary>
        /// Monitor sink: (buffer, floatCount, sampleRate), interleaved
        /// stereo, called on the audio callback thread. Must copy the data
        /// synchronously and return quickly.
        /// </summary>
        public Action<float[], int, uint> MonitorSink { get; set; }

        private volatile int _monitorMode = (int)MonitorModes.Off;

        /// <summary>What the monitor tap plays. Live-switchable while
        /// transmitting.</summary>
        public MonitorModes MonitorMode
        {
            get { return (MonitorModes)_monitorMode; }
            set { _monitorMode = (int)value; }
        }

        private volatile bool _bypassAll;

        /// <summary>
        /// Policy bypass for non-voice modes (CW, digital): the whole chain
        /// steps aside, monitor included. Set by the app on mode changes —
        /// RNNoise is speech-trained and a gate has no business shaping data
        /// audio.
        /// </summary>
        public bool BypassAll
        {
            get { return _bypassAll; }
            set { _bypassAll = value; }
        }

        // Scratch: the pre-processing copy of the input (for the residual)
        // and the buffer handed to the monitor sink.
        private float[] _inputCopy = Array.Empty<float>();
        private float[] _monitorBuf = Array.Empty<float>();

        /// <summary>
        /// Process one buffer of interleaved stereo float samples in place.
        /// Matches the TxAudioProcessorCallback contract; FlexBase hands this
        /// method to the input stream.
        /// </summary>
        public void Process(float[] buffer, int count, uint sampleRate)
        {
            if (_bypassAll) return;

            var nr = NoiseReducer;
            var sink = MonitorSink;
            var mode = (MonitorModes)_monitorMode;
            bool wantMonitor = mode != MonitorModes.Off && sink != null;
            bool anyStage = nr != null || Gate.Enabled;

            // Nothing attached, nothing monitored: stay entirely out of the
            // way. (The gate still wants its clock stamped so enabling it
            // mid-stream is not mistaken for a key-down; Process with the
            // gate disabled does exactly that and nothing else.)
            if (!anyStage && !wantMonitor)
            {
                Gate.Process(buffer, 0, sampleRate);
                return;
            }

            // Keep the untouched input while the stages modify the buffer —
            // the residual is a subtraction and needs both ends.
            if (_inputCopy.Length < count) _inputCopy = new float[count];
            Array.Copy(buffer, _inputCopy, count);

            // NR first, gate second — see class remarks.
            nr?.Invoke(buffer, count, sampleRate);
            Gate.Process(buffer, count, sampleRate);

            if (!wantMonitor) return;

            if (_monitorBuf.Length < count) _monitorBuf = new float[count];
            switch (mode)
            {
                case MonitorModes.Output:
                    Array.Copy(buffer, _monitorBuf, count);
                    break;
                case MonitorModes.Residual:
                    for (int i = 0; i < count; i++)
                        _monitorBuf[i] = _inputCopy[i] - buffer[i];
                    break;
                case MonitorModes.Split:
                    // Left = output, right = residual. The stream carries the
                    // mono mic duplicated on both channels, so channel 0 of
                    // each pair is a faithful mono rendering of either signal.
                    for (int i = 0; i + 1 < count; i += 2)
                    {
                        _monitorBuf[i] = buffer[i];
                        _monitorBuf[i + 1] = _inputCopy[i] - buffer[i];
                    }
                    break;
            }
            sink(_monitorBuf, count, sampleRate);
        }
    }
}
