using System;
using System.Diagnostics;
using System.Threading;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// PC-side audio processing pipeline for RX audio.
    /// Chains processing stages between Opus decode and PortAudio playback:
    /// Capture listens first, then the spectral and neural slots run, followed
    /// by the warning duck and recording playback level.
    ///
    /// Works on ALL radios (6000/8000/Aurora) since processing runs on the PC,
    /// unlike radio-side NR which requires 8000/Aurora hardware DSP.
    ///
    /// Designed as the single insertion point for all PC-side audio processing.
    /// Future stages (waterfall FFT tap, recording tap, DSP filters) plug in here.
    ///
    /// Thread safety: Process() is called from FlexBase's remoteAudioProc thread.
    /// Control state is published by reference. The audio thread owns both
    /// slots while the interlocked process mark is held; disconnect waits
    /// before freeing their native state.
    /// Sprint 25 Phase 20.
    /// </summary>
    public class RxAudioPipeline : IDisposable
    {
        private SpectralSubtractionProvider _spectralSub;
        private SpectralSubtractionProvider _processingCapture;
        private readonly Action<PerChannelDenoiserBank> _prepareSpectral;
        private readonly DenoiseEngineCatalog _catalog;
        public PcDenoiseSlot SpectralSlot { get; }
        public PcDenoiseSlot NeuralSlot { get; }
        private int _processMark; // 0 idle, 1 inside Process, 2 permanently closed
        private int _disposed;
        private string _mode = "";
        public event EventHandler<DenoiseSelectionResult>? SelectionResult;
        public string CurrentMode => Volatile.Read(ref _mode);
        public bool SelectPcEngine(string id, out string? refusal)
        {
            var row = _catalog.Find(id);
            if (row == null) { refusal = "unknown-engine"; return false; }
            if (row.Side != DenoiseSide.Pc) { refusal = "radio-engine"; return false; }
            return (row.Family == DenoiseFamily.Spectral ? SpectralSlot : NeuralSlot).Select(id, out refusal);
        }
        public bool SelectPcEngine(string id) => SelectPcEngine(id, out _);
        // THE MODE GATE IS RETIRED (Sprint 49 Track E; ruled by Noel 2026-11-07,
        // question 4: "retire the switch"). "PC Neural NR Voice Modes Only" held
        // the neural slot off in CW and both slots off in digital modes. Per-mode
        // memory replaced it: no engine is locked out of any mode, and each mode
        // group remembers what the operator last used in it (NoiseController).
        // So the slots run whatever is on, in every mode.
        private void ForwardSelection(object? sender, DenoiseSelectionResult result) => SelectionResult?.Invoke(this, result);
        public string CaptureFinishKey() => CaptureFinishKey(SpectralEnabled, SpectralSlot.ChosenEngineId);
        /// <summary>
        /// What the capture's finish sentence says next. No mode holds the
        /// spectral slot back any more, so the "held by mode" fragment is gone:
        /// it would now be untrue.
        /// </summary>
        public static string CaptureFinishKey(bool on, string selection) =>
            on && selection == "spectral_capture" ? "audio.noise.capture.spectral_is_using_it" : "audio.noise.capture.turn_spectral_on";
        private readonly int _channels;

        /// <summary>Kept for the duck's ramp, which has to convert a time
        /// constant into a per-frame increment.</summary>
        private readonly int _sampleRate;

        /// <summary>
        /// Create the pipeline for the given audio format.
        /// Both providers are created in standalone mode (no ISampleProvider source).
        /// </summary>
        public RxAudioPipeline(int sampleRate = 48000, int channels = 2, DenoiseEngineCatalog? catalog = null)
        {
            _channels = channels;
            _sampleRate = sampleRate;
            _catalog = catalog ?? new DenoiseEngineCatalog();
            SpectralSlot = new(DenoiseFamily.Spectral, "spectral_capture", sampleRate, channels, _catalog);
            NeuralSlot = new(DenoiseFamily.Neural, "rnnoise", sampleRate, channels, _catalog);
            SpectralSlot.SelectionResult += ForwardSelection;
            NeuralSlot.SelectionResult += ForwardSelection;
            _spectralSub = new SpectralSubtractionProvider(sampleRate, channels);
            _processingCapture = _spectralSub;
            _prepareSpectral = PrepareSpectral;

            Trace.WriteLine($"RxAudioPipeline: created ({sampleRate}Hz, {channels}ch)");
        }

        // --- RNNoise properties ---

        /// <summary>Enable/disable PC-side neural noise reduction (RNNoise).</summary>
        public bool RnnEnabled
        {
            get => NeuralSlot.Enabled;
            set => NeuralSlot.Enabled = value;
        }

        /// <summary>RNNoise wet/dry mix: 0.0 = bypass, 1.0 = fully processed. Default 0.8.</summary>
        public float RnnStrength
        {
            get => NeuralSlot.GetStrength("rnnoise");
            set => NeuralSlot.SetStrength("rnnoise", value);
        }

        // --- Spectral subtraction properties ---

        /// <summary>Enable/disable PC-side spectral subtraction.</summary>
        public bool SpectralEnabled
        {
            get => SpectralSlot.Enabled;
            set => SpectralSlot.Enabled = value;
        }

        /// <summary>Subtraction aggressiveness: 0.0 = none, 1.0 = max. Default 0.7.</summary>
        public float SpectralStrength
        {
            get => (float)SpectralSlot.GetSetting("spectral_capture", "subtraction_amount").Number;
            set => SpectralSlot.TrySetSetting("spectral_capture", "subtraction_amount", new EngineSettingValue(Math.Round(Math.Clamp(value, 0f, 1f), 2)));
        }

        /// <summary>Spectral floor to prevent musical noise artifacts. Default 0.02.</summary>
        public float SpectralFloor
        {
            get => (float)SpectralSlot.GetSetting("spectral_capture", "spectral_floor").Number;
            set => SpectralSlot.TrySetSetting("spectral_capture", "spectral_floor", new EngineSettingValue(Math.Round(Math.Clamp(value, 0f, 0.2f), 3)));
        }

        /// <summary>True while actively sampling noise profile.</summary>
        public bool IsNoiseSampling => _spectralSub.IsSampling;

        /// <summary>Noise sampling progress 0.0-1.0.</summary>
        public float NoiseSamplingProgress => _spectralSub.SamplingProgress;

        /// <summary>True if a noise profile has been captured.</summary>
        public bool HasNoiseProfile => _spectralSub.HasProfile;

        /// <summary>Name of the loaded noise profile.</summary>
        public string NoiseProfileName => _spectralSub.ProfileName;

        // --- Mode tracking ---

        /// <summary>
        /// Set the current radio mode (SSB, CW, etc.) so RNNoise can
        /// auto-disable for non-voice modes. Called by the UI when mode changes.
        /// </summary>
        public void SetCurrentMode(string mode)
        {
            Volatile.Write(ref _mode, mode ?? "");
        }

        // --- Noise profile management ---

        /// <summary>Start sampling noise. Call when the band is quiet.
        /// Default 3 seconds (range 1-5) per the ratified capture-window decision.</summary>
        public void StartNoiseSampling(int durationSeconds = 3)
        {
            var old = Volatile.Read(ref _spectralSub);
            var fresh = new SpectralSubtractionProvider(_sampleRate, _channels) { ProfileName = old.ProfileName };
            for (int ch = 0; ch < _channels; ch++) fresh.SetChannelProfile(ch, old.GetChannelProfile(ch));
            fresh.StartSampling(durationSeconds);
            Interlocked.Exchange(ref _spectralSub, fresh);
        }

        /// <summary>Cancel an in-progress noise sampling session.</summary>
        public void CancelNoiseSampling()
        {
            _spectralSub.CancelSampling();
        }

        /// <summary>Load a noise profile from file.</summary>
        public bool LoadNoiseProfile(string filePath)
        {
            return _spectralSub.LoadProfile(filePath);
        }

        /// <summary>Save the current noise profile to file.</summary>
        public bool SaveNoiseProfile(string filePath, string name, string band = "", string antenna = "")
        {
            return _spectralSub.SaveProfile(filePath, name, band, antenna);
        }

        /// <summary>Clear the current noise profile.</summary>
        public void ClearNoiseProfile()
        {
            _spectralSub.ClearProfile();
        }

        // --- Core processing ---

        /// <summary>
        /// Process a decoded audio buffer in-place.
        /// Capture listens to raw input, then the spectral slot and neural slot
        /// run before the warning duck and recording playback level.
        ///
        /// Called from FlexBase.remoteAudioProc thread via the PostDecodeProcessor delegate.
        /// </summary>
        public void Process(float[] buffer)
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _processMark, 1, 0) != 0) return;
            try
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                int count = buffer.Length;
                var capture = Volatile.Read(ref _spectralSub);
                bool capturing = capture.IsSampling;
                capture.ProcessInPlace(buffer, 0, count, _channels); // capture only, never enabled
                _processingCapture = capture;
                SpectralSlot.Process(buffer, true, capture.HasProfile, capturing, _prepareSpectral);
                NeuralSlot.Process(buffer, true, true, false, null);
                ApplyDuck(buffer, count);
                ApplyPlaybackLevel(buffer, count);
            }
            finally { Interlocked.Exchange(ref _processMark, 0); }
        }

        private void PrepareSpectral(PerChannelDenoiserBank bank)
        {
            for (int ch = 0; ch < bank.Channels; ch++)
                if (bank.EngineFor(ch) is SpectralCaptureEngine engine)
                    engine.SetProfile(_processingCapture.GetChannelProfile(ch));
        }

        // --- Recording playback level (#787, bench 2026-10-25) ---
        //
        // After the warning duck, and separate from it: the duck is
        // warnings-only by ruling, and this one follows a recording playing on
        // this computer. The two multiply, which is right — a warning during
        // playback still dips the band further. See RxPlaybackLevel.

        private float _playbackGain = 1f;

        private void ApplyPlaybackLevel(float[] buffer, int count) =>
            _playbackGain = GlideGain(buffer, count, Math.Max(_channels, 1), _sampleRate,
                _playbackGain, RxPlaybackLevel.TargetGain, Math.Max(6f, RxPlaybackLevel.DepthDb));

        private const float FloorDb = -60f;

        private static float ToDb(float gain) => gain <= 0.001f ? FloorDb : 20f * MathF.Log10(gain);

        /// <summary>
        /// Ramp <paramref name="buffer"/> from <paramref name="gain"/> toward
        /// <paramref name="target"/>, per frame, and return where it got to.
        /// The ramp moves in DECIBELS, at a rate that crosses
        /// <paramref name="depthDb"/> in <see cref="RxPlaybackLevel.AttackMs"/>
        /// going down and <see cref="RxPlaybackLevel.ReleaseMs"/> coming back.
        /// A straight ramp in amplitude sounds like a jump at its start, because
        /// the ear hears loudness in decibels (retest 2026-10-25: "a bit of a
        /// blip as it brings the band up"). Ramped, never stepped, for the
        /// reason on <see cref="ApplyDuck"/>.
        /// </summary>
        internal static float GlideGain(float[] buffer, int count, int channels, float sampleRate,
            float gain, float target, float depthDb)
        {
            if (target >= 0.9999f && gain >= 0.9999f) return 1f;

            // Coming back to full is the band's half of the crossfade, so it is
            // the mirror of the recording's equal-power cosine: a sine in
            // amplitude, quick at first, from the lowered level to 1 over
            // ReleaseMs. The decibel curve rose slowly at first and left "a
            // slight dip in volume" (bench 2026-10-25 21:19). Where the glide
            // is on that sine is read back from the gain itself, so nothing
            // else has to be remembered between buffers.
            if (target >= 0.9999f && gain < target)
            {
                float low = MathF.Pow(10f, -Math.Max(0f, depthDb) / 20f);
                if (depthDb >= 59f) low = 0f; // muted
                float span = 1f - low;
                float t = span <= 0f ? 1f
                    : MathF.Asin(Math.Clamp((gain - low) / span, 0f, 1f)) * 2f / MathF.PI;
                float advance = 1f / Math.Max(1f, sampleRate * (RxPlaybackLevel.ReleaseMs / 1000f));
                for (int i = 0; i < count; i += channels)
                {
                    t = Math.Min(1f, t + advance);
                    gain = t >= 1f ? 1f : low + span * MathF.Sin(t * MathF.PI / 2f);
                    for (int c = 0; c < channels && i + c < count; c++)
                        buffer[i + c] *= gain;
                }
                return gain;
            }

            float db = ToDb(gain), targetDb = ToDb(target);
            float ms = targetDb < db ? RxPlaybackLevel.AttackMs : RxPlaybackLevel.ReleaseMs;
            float step = depthDb / Math.Max(1f, sampleRate * (ms / 1000f));
            for (int i = 0; i < count; i += channels)
            {
                if (db > targetDb) db = Math.Max(targetDb, db - step);
                else if (db < targetDb) db = Math.Min(targetDb, db + step);
                // Arrived: exactly the target, so muted is a true zero.
                gain = db == targetDb ? target : db <= FloorDb ? 0f : MathF.Pow(10f, db / 20f);
                for (int c = 0; c < channels && i + c < count; c++)
                    buffer[i + c] *= gain;
            }
            return gain;
        }

        // --- Warning duck (#116) ---
        //
        // Last in the chain on purpose. The noise reducers are level-sensitive
        // — spectral subtraction works against a measured noise floor, and
        // RNNoise was trained on speech at natural levels — so pulling the
        // signal down in front of them would move the floor they are working
        // against every time a warning sounded. The duck is a listening
        // adjustment, not part of the cleanup, so it goes after both.

        /// <summary>Where the duck gain is right now, glided per frame toward
        /// <see cref="RxDuck.TargetGain"/>.</summary>
        private float _duckGain = 1f;

        /// <summary>
        /// Ramp the buffer toward the duck's target gain.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Ramped, never stepped.</b> A hard gain change mid-buffer is a
        /// discontinuity, and a discontinuity is a click — precisely the
        /// artifact class this whole area of the app exists to remove. So the
        /// gain moves by a fixed increment per FRAME (not per sample, or a
        /// stereo stream would ramp twice as fast as a mono one).
        /// </para>
        /// <para>
        /// The increment is sized so a full excursion takes
        /// <see cref="RxDuck.AttackMs"/> going down and
        /// <see cref="RxDuck.ReleaseMs"/> coming back. Both are read here on
        /// every buffer rather than cached, because since #535 they follow the
        /// operator's timing preset live — and the release also grows with
        /// the depth, so a deeper dip comes back at the same RATE rather than
        /// in the same time. Changing the depth changes how deep it goes and
        /// how long it takes to come home, never how steep the return is.
        /// </para>
        /// <para>
        /// <b>It converges on 1.0 whenever nothing is asking for a duck</b>,
        /// including after a request expires, after ducking is switched off
        /// mid-duck, and after anything at all goes wrong upstream. There is
        /// no state that has to be unwound; the only way to stay attenuated is
        /// for something to keep asking.
        /// </para>
        /// </remarks>
        private void ApplyDuck(float[] buffer, int count)
        {
            float target = RxDuck.TargetGain;

            // Nothing to do: not ducking, and not still on the way home.
            if (target >= 0.9999f && _duckGain >= 0.9999f) return;

            int channels = Math.Max(_channels, 1);
            float span = 1f - RxDuck.DuckedGain;
            if (span < 0.0001f) span = 1f; // depth of zero: glide home promptly

            float ms = target < _duckGain ? RxDuck.AttackMs : RxDuck.ReleaseMs;
            float step = span / Math.Max(1f, _sampleRate * (ms / 1000f));

            for (int i = 0; i < count; i += channels)
            {
                if (_duckGain > target) _duckGain = Math.Max(target, _duckGain - step);
                else if (_duckGain < target) _duckGain = Math.Min(target, _duckGain + step);

                for (int c = 0; c < channels && i + c < count; c++)
                    buffer[i + c] *= _duckGain;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            SpectralSlot.CloseAdmission(); NeuralSlot.CloseAdmission();
            var wait = Stopwatch.StartNew();
            var spin = new SpinWait();
            while (Interlocked.CompareExchange(ref _processMark, 2, 0) != 0)
            {
                if (wait.ElapsedMilliseconds >= 250)
                {
                    Trace.WriteLine("RxAudioPipeline: disconnect timed out; retaining engines rather than freeing during Process");
                    return;
                }
                spin.SpinOnce();
            }
            SpectralSlot.DisposeOwned(); NeuralSlot.DisposeOwned();
            Trace.WriteLine("RxAudioPipeline: disposed");
        }
    }
}
