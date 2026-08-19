using System;
using System.Diagnostics;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// App-side coordinator for the TX conditioning chain (Track I). FlexBase
    /// owns the chain itself (TxAudioConditioner: gate + pluggable NR slot +
    /// monitor tap, running in the PortAudio input callback); this class owns
    /// everything the chain must not know about:
    ///
    ///  * the RNNoise engine (NoiseReductionProvider, standalone instance —
    ///    the SAME engine the receive pipeline uses; the algorithm does not
    ///    care which direction audio flows),
    ///  * the residual monitor's local playback (TxMonitorPlayer),
    ///  * the threshold policy: the gate threshold is DERIVED from the
    ///    measured noise floor (LufsMeter.Profile.NoiseFloorLufs, the same
    ///    figure the Microphone Check reports), floor + 6..10 dB, refreshed
    ///    a couple of times a second while the floor estimate improves. A
    ///    fixed-dB gate is what a podcast plugin ships because it cannot
    ///    know your room; this app knows the room.
    ///  * mode policy: the whole chain steps aside in CW/digital modes
    ///    (RNNoise is speech-trained; a gate has no business shaping data).
    ///
    /// Lifecycle mirrors the RX pipeline: ScreenFieldsPanel.SetRig calls
    /// Attach, Detach on disconnect.
    ///
    /// SETTINGS NOTE (Track F coordination): gate and NR settings belong to
    /// the MICROPHONE PROFILE, not the app — a gate tuned for a headset in a
    /// quiet room is wrong for a desk mic in a noisy one. The serializable
    /// shape is <see cref="TxConditioningSettings"/>; this class deliberately
    /// creates NO store of its own. Until Track F's profile structure lands,
    /// settings are session-only.
    /// </summary>
    public static class TxAudioConditioning
    {
        private static FlexBase _rig;
        private static NoiseReductionProvider _nr;
        private static TxMonitorPlayer _player;
        private static System.Timers.Timer _policyTimer;

        private static volatile float _thresholdMarginDb = 8f;
        private static volatile bool _autoThreshold = true;

        /// <summary>The chain on the current rig, or null when detached.</summary>
        public static JJPortaudio.TxAudioConditioner Conditioner => _rig?.TxConditioner;

        /// <summary>
        /// Wire the conditioning chain of the given rig: plug the NR engine
        /// and monitor playback into it and start the policy tick. Safe to
        /// call again on reconnect.
        /// </summary>
        public static void Attach(FlexBase rig)
        {
            Detach();
            if (rig == null) return;
            _rig = rig;

            _nr = new NoiseReductionProvider(48000, 2)
            {
                Enabled = false,       // operator opts in
                Strength = 0.8f
            };
            _player = new TxMonitorPlayer();

            var cond = rig.TxConditioner;
            cond.NoiseReducer = NrStage;
            cond.MonitorSink = MonitorStage;

            rig.ModeChanged += OnModeChanged;
            ApplyModePolicy(rig.Mode ?? "");

            // Twice a second: refresh the derived threshold from the live
            // noise-floor estimate. Cheap — the profile is cached against
            // the meter's block count and only recomputes while transmitting,
            // which is exactly when the floor estimate is improving.
            _policyTimer = new System.Timers.Timer(500) { AutoReset = true };
            _policyTimer.Elapsed += (s, e) => RefreshDerivedThreshold();
            _policyTimer.Start();

            Trace.WriteLine("TxAudioConditioning: attached");
        }

        /// <summary>Unhook from the rig and release the monitor device.</summary>
        public static void Detach()
        {
            var rig = _rig;
            _rig = null;
            if (rig != null)
            {
                rig.ModeChanged -= OnModeChanged;
                var cond = rig.TxConditioner;
                cond.NoiseReducer = null;
                cond.MonitorSink = null;
                cond.MonitorMode = JJPortaudio.TxAudioConditioner.MonitorModes.Off;
            }
            _policyTimer?.Stop();
            _policyTimer?.Dispose();
            _policyTimer = null;
            _player?.Dispose();
            _player = null;
            _nr?.Dispose();
            _nr = null;
        }

        // ------------------------------------------------------------------
        // Stages (audio callback thread)
        // ------------------------------------------------------------------

        private static void NrStage(float[] buffer, int count, uint sampleRate)
        {
            var nr = _nr;
            if (nr == null) return;
            // RNNoise is 48 kHz-native (480-sample frames are 10 ms only
            // there). If the device fell back to another rate, bypass
            // honestly rather than denoise wrongly — and honestly means the
            // residual goes silent, which is the designed tell that the
            // stage is not live.
            if (sampleRate == 48000)
                nr.ProcessInPlace(buffer, 0, count, 2);
        }

        private static void MonitorStage(float[] buffer, int count, uint sampleRate)
        {
            _player?.Push(buffer, count, (int)sampleRate);
        }

        // ------------------------------------------------------------------
        // Policy
        // ------------------------------------------------------------------

        /// <summary>
        /// Derive the gate threshold from the measured noise floor:
        /// floor + margin, from the same LufsMeter profile the Microphone
        /// Check reports. No-op until the meter has enough transmitted audio
        /// (~3 seconds of speech) to know the floor; before that the gate
        /// keeps its deliberately inert default.
        /// </summary>
        private static void RefreshDerivedThreshold()
        {
            var rig = _rig;
            if (rig == null || !_autoThreshold) return;
            try
            {
                var profile = rig.TxLoudnessProfile;
                if (!profile.IsValid) return;
                if (profile.NoiseFloorLufs <= JJPortaudio.LufsMeter.Floor) return;
                rig.TxConditioner.Gate.ThresholdDb =
                    profile.NoiseFloorLufs + _thresholdMarginDb;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("TxAudioConditioning: threshold refresh failed: " + ex.Message);
            }
        }

        private static void OnModeChanged(string newMode)
        {
            ApplyModePolicy(newMode ?? "");
        }

        private static void ApplyModePolicy(string mode)
        {
            var rig = _rig;
            if (rig == null) return;
            rig.TxConditioner.BypassAll = IsNonVoiceMode(mode);
            var nr = _nr;
            if (nr != null) nr.CurrentMode = mode; // defense in depth
        }

        /// <summary>
        /// Same test NoiseReductionProvider applies internally (its copy is
        /// private; duplicated here rather than widening a symbol another
        /// track may be relying on — flagged in the track report).
        /// </summary>
        private static bool IsNonVoiceMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return false;
            return mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase) ||
                   mode.StartsWith("DIG", StringComparison.OrdinalIgnoreCase) ||
                   mode.StartsWith("FDM", StringComparison.OrdinalIgnoreCase) ||
                   mode.Equals("RTTY", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Operator-facing surface (UI binds to these)
        // ------------------------------------------------------------------

        /// <summary>PC-side transmit noise reduction on/off.</summary>
        public static bool NrEnabled
        {
            get { return _nr?.Enabled == true; }
            set { var nr = _nr; if (nr != null) nr.Enabled = value; }
        }

        /// <summary>
        /// NR strength, 0..1 wet/dry. LIVE while monitoring — hear your own
        /// voice in the residual, turn this down, hear it gone. That loop is
        /// the whole point of the residual monitor.
        /// </summary>
        public static float NrStrength
        {
            get { return _nr?.Strength ?? 0.8f; }
            set { var nr = _nr; if (nr != null) nr.Strength = Math.Clamp(value, 0f, 1f); }
        }

        /// <summary>Transmit noise gate on/off.</summary>
        public static bool GateEnabled
        {
            get { return Conditioner?.Gate.Enabled == true; }
            set { var c = Conditioner; if (c != null) c.Gate.Enabled = value; }
        }

        /// <summary>
        /// Margin above the measured noise floor for the derived threshold,
        /// dB (6..10 recommended; clamped 3..15).
        /// </summary>
        public static float ThresholdMarginDb
        {
            get { return _thresholdMarginDb; }
            set { _thresholdMarginDb = Math.Clamp(value, 3f, 15f); }
        }

        /// <summary>Derive the threshold from the measured floor (default) or
        /// leave it wherever the operator set it by hand.</summary>
        public static bool AutoThreshold
        {
            get { return _autoThreshold; }
            set { _autoThreshold = value; }
        }

        /// <summary>What the monitor plays: Off, Output, Residual, or Split
        /// (output left, residual right). Live-switchable while transmitting.</summary>
        public static JJPortaudio.TxAudioConditioner.MonitorModes MonitorMode
        {
            get
            {
                return Conditioner?.MonitorMode
                    ?? JJPortaudio.TxAudioConditioner.MonitorModes.Off;
            }
            set
            {
                var c = Conditioner;
                if (c == null) return;
                c.MonitorMode = value;
                if (value == JJPortaudio.TxAudioConditioner.MonitorModes.Off)
                    _player?.Stop();
                else
                    // Open the output device now, on this thread, so the
                    // first monitored buffer never pays the device-open cost
                    // inside the audio callback.
                    _player?.Prewarm();
            }
        }

        /// <summary>Monitor playback volume, 0..1.</summary>
        public static float MonitorVolume
        {
            get { return _player?.Volume ?? 1f; }
            set { var p = _player; if (p != null) p.Volume = value; }
        }

        /// <summary>Capture the current knobs as a settings object (the shape
        /// Track F's microphone profile will carry).</summary>
        public static TxConditioningSettings CaptureSettings()
        {
            var gate = Conditioner?.Gate;
            return new TxConditioningSettings
            {
                NrEnabled = NrEnabled,
                NrStrength = NrStrength,
                GateEnabled = GateEnabled,
                AutoThreshold = AutoThreshold,
                ThresholdMarginDb = ThresholdMarginDb,
                GateThresholdDb = gate?.ThresholdDb ?? JJPortaudio.TxNoiseGate.DefaultThresholdDb,
                GateAttackMs = gate?.AttackMs ?? 3f,
                GateHoldMs = gate?.HoldMs ?? 150f,
                GateReleaseMs = gate?.ReleaseMs ?? 200f,
                GateRangeDb = gate?.RangeDb ?? 25f
            };
        }

        /// <summary>Apply a settings object to the live chain.</summary>
        public static void ApplySettings(TxConditioningSettings s)
        {
            if (s == null) return;
            NrEnabled = s.NrEnabled;
            NrStrength = s.NrStrength;
            GateEnabled = s.GateEnabled;
            AutoThreshold = s.AutoThreshold;
            ThresholdMarginDb = s.ThresholdMarginDb;
            var gate = Conditioner?.Gate;
            if (gate != null)
            {
                if (!s.AutoThreshold) gate.ThresholdDb = s.GateThresholdDb;
                gate.AttackMs = s.GateAttackMs;
                gate.HoldMs = s.GateHoldMs;
                gate.ReleaseMs = s.GateReleaseMs;
                gate.RangeDb = s.GateRangeDb;
            }
        }

        /// <summary>Back to the recommended state: gate and NR as shipped,
        /// threshold derived from the floor again.</summary>
        public static void ResetToRecommended()
        {
            ApplySettings(new TxConditioningSettings());
            var gate = Conditioner?.Gate;
            if (gate != null) gate.ThresholdDb = JJPortaudio.TxNoiseGate.DefaultThresholdDb;
            RefreshDerivedThreshold();
        }
    }

    // TxConditioningSettings moved to Radios\MicrophoneProfile.cs
    // (Track B, 2026-08-18, #44): it is the microphone profile's payload
    // now, exactly as the note it carried here always said it should be.
    // Same name and fields; `using Radios;` above keeps every reference in
    // this file compiling unchanged.
}
