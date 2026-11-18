using System;
using System.Collections.Generic;
using System.Diagnostics;
using Radios;

namespace JJFlexWpf
{
    /// <summary>How applying a named transmit noise setup ended.</summary>
    public enum TxSetupApplyOutcome
    {
        /// <summary>The setup was found and put in place (its slot changes wait for the unkey when keyed).</summary>
        Applied,
        /// <summary>
        /// This radio has no setup of that name: nothing was applied and the
        /// chain kept its last values (ruled 2026-11-07 07:52). Said once,
        /// when <see cref="TxSetupApplyResult.FirstTime"/>.
        /// </summary>
        Missing,
        /// <summary>No name was given, or no settings root; nothing happened.</summary>
        NoName,
    }

    /// <summary>The outcome of <see cref="TxAudioConditioning.ApplyNamedSetup"/>.</summary>
    public sealed record TxSetupApplyResult(TxSetupApplyOutcome Outcome, bool FirstTime = false, TxChangeTiming Timing = TxChangeTiming.Now);

    /// <summary>
    /// App-side coordinator for the transmit conditioning chain (Track I;
    /// Sprint 49 Track H). FlexBase owns the chain itself (TxAudioConditioner:
    /// noise reduction seam, gate and monitor tap, running in the PortAudio
    /// input callback); this class owns everything the chain must not know
    /// about:
    ///
    ///  * the transmit noise reduction: <see cref="TxNoiseChain"/>, two
    ///    transmit slots (spectral, neural) from the same slot class and
    ///    catalog receive uses (Sprint 49 Track H, item A),
    ///  * the residual monitor's local playback (TxMonitorPlayer),
    ///  * the threshold policy: the gate threshold is DERIVED from the
    ///    measured noise floor (LufsMeter.Profile.NoiseFloorLufs, the same
    ///    figure the Microphone Check reports), floor + 6..10 dB, refreshed
    ///    a couple of times a second while the floor estimate improves. A
    ///    fixed-dB gate is what a podcast plugin ships because it cannot
    ///    know your room; this app knows the room.
    ///  * mode policy: the whole chain steps aside in CW/digital modes
    ///    (there is no microphone audio to clean; a gate has no business
    ///    shaping data).
    ///  * the key state the chain reads: our Mox OR the radio's interlock.
    ///
    /// <para><b>The operator's choices outlive the connection</b> (#1077,
    /// settings are intents). The noise chain and the gate's settings are held
    /// here for the session whether or not a radio is connected; a toggle made
    /// with no radio is kept and applied when one connects, never refused.</para>
    ///
    /// <para><b>Where the choices are stored</b> (item F): a named transmit
    /// noise setup per radio (<see cref="TxNoiseSetupStore"/>), pointed at by a
    /// microphone setup. <see cref="CaptureSetup"/> and <see cref="ApplySetup"/>
    /// move between this live state and a setup.</para>
    /// </summary>
    public static class TxAudioConditioning
    {
        private static readonly object _gate = new();
        private static FlexBase _rig;
        private static TxNoiseChain _chain;
        private static int _nrAppliedLatency;
        private static TxMonitorPlayer _player;
        private static System.Timers.Timer _policyTimer;

        private static volatile float _thresholdMarginDb = 8f;
        private static volatile bool _autoThreshold = true;

        // The gate's settings as an intent (#1077): held with or without a
        // radio, copied onto the rig's gate whenever one is attached.
        private static TxNoiseGateSetup _gateIntent = new();

        /// <summary>The chain on the current rig, or null when detached.</summary>
        public static JJPortaudio.TxAudioConditioner Conditioner => _rig?.TxConditioner;

        /// <summary>
        /// The transmit noise reduction for this session (item B), made on first
        /// use and kept across connections, so a choice made with no radio is
        /// held (#1077). Its slots build their engines on worker threads.
        /// </summary>
        public static TxNoiseChain Chain
        {
            get
            {
                var chain = _chain;
                if (chain != null) return chain;
                lock (_gate)
                {
                    if (_chain != null) return _chain;
                    var made = CreateChain();
                    HookNotices(made);
                    return _chain = made;
                }
            }
        }

        /// <summary>How the mid-over fallback notice is given (repair round 1, R1).</summary>
        public enum TxNoticeRoute { ToneAndWords, WordsOnlyNoAlertChannel, WordsOnlyCaptureHearsOutput }

        /// <summary>
        /// The tone only on the alert channel, and only when the transmit
        /// microphone is not a wire that could carry it back (a loopback or a
        /// virtual cable, or a Windows default that is one). Words otherwise.
        /// This closes the routes the app can see. A loudspeaker the microphone
        /// hears in the room is physics, and screen reader speech has the same
        /// exposure; nothing here claims to solve it.
        /// </summary>
        internal static TxNoticeRoute ChooseNoticeRoute(bool alertChannelLive, string captureDevice, string windowsDefaultInput)
        {
            if (!alertChannelLive) return TxNoticeRoute.WordsOnlyNoAlertChannel;
            if (JJPortaudio.Devices.CapturesComputerOutput(captureDevice ?? "", windowsDefaultInput)) return TxNoticeRoute.WordsOnlyCaptureHearsOutput;
            return TxNoticeRoute.ToneAndWords;
        }

        /// <summary>
        /// Wire a chain's two reports to the operator and the trace: a slot that
        /// fell back mid-over (item C) and each over's frame counts (item D).
        /// </summary>
        internal static void HookNotices(TxNoiseChain chain)
        {
            chain.FellBack += (family, engineId) => TellFellBack(chain, engineId);
            chain.OverReport += line => Trace.WriteLine(line);
        }

        /// <summary>
        /// The mid-over failure notice (item C; Noel 2026-11-18 08:18 and 08:21:
        /// "Wouldn't transmit a beep on air", then "the one line or few words
        /// with a tone (short beep)"). A short earcon on the operator's ALERT
        /// channel, the existing calm "something failed" tone, and a few words.
        /// What this guarantees, and no more (repair round 1, R1): the tone is
        /// never written into the transmit buffers (the earcon player owns its
        /// own mixer and device; transmit frames are built only by
        /// <c>TxFramePipeline</c>); it plays ONLY on the alert channel, never the
        /// system-beep fallback, so with no alert output it is words only; and
        /// it is not played when the transmit microphone is a loopback or a
        /// virtual cable that could carry it back by wire
        /// (<see cref="ChooseNoticeRoute"/>). A loudspeaker the microphone hears
        /// across the room is acoustic, as screen reader speech is, and is not
        /// solved here. The route taken is traced every time.
        /// </summary>
        private static void TellFellBack(TxNoiseChain chain, string engineId)
        {
            try
            {
                var route = ChooseNoticeRoute(EarconPlayer.AlertChannelLive, _rig?.TransmitCaptureDeviceName ?? "", null);
                bool toned = route == TxNoticeRoute.ToneAndWords && EarconPlayer.ProblemRecordedToneOnAlertChannelOnly();
                Trace.WriteLine("TxAudioConditioning: transmit fallback notice: "
                    + (toned ? "tone on the alert channel and words"
                       : route == TxNoticeRoute.WordsOnlyNoAlertChannel ? "words only: the alert channel has no output, and the system beep is never used for this"
                       : route == TxNoticeRoute.WordsOnlyCaptureHearsOutput ? "words only: the transmit microphone is a loopback or virtual cable that could carry the tone"
                       : "words only: the tone did not play (warning sounds off, or the alert channel went away)"));
                var row = chain.Catalog.Find(engineId);
                string engine = row != null ? Lexicon.Get(row.LabelKey) : engineId;
                ScreenReaderOutput.Speak(Lexicon.Get("audio.denoise.tx.fell_back", ("engine", engine)),
                    Radios.Speech.SpeechIntent.Queue, VerbosityLevel.Terse,
                    subject: Radios.Speech.SpeechSubject.TransmitNoiseFallback);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("TxAudioConditioning: the fallback notice could not be given: " + ex.Message);
            }
        }

        /// <summary>A new transmit chain on the app's catalog, its neural slot on RNNoise at the transmit default floor (S8), both slots off.</summary>
        internal static TxNoiseChain CreateChain(DenoiseEngineCatalog catalog = null) => new TxNoiseChain(catalog);

        /// <summary>Tests only: put a chain in place (null to drop it) without attaching a rig.</summary>
        internal static void UseChainForTest(TxNoiseChain chain) { lock (_gate) _chain = chain; }

        /// <summary>
        /// Wire the conditioning chain of the given rig: plug the noise
        /// reduction, the key state and the monitor playback into it, put the
        /// held gate settings on its gate, and start the policy tick. Safe to
        /// call again on reconnect.
        /// </summary>
        public static void Attach(FlexBase rig)
        {
            Detach();
            if (rig == null) return;
            _rig = rig;

            var chain = Chain;
            _player = new TxMonitorPlayer();

            var cond = rig.TxConditioner;
            // Keyed means our Mox OR the radio's interlock (item B): VOX keys
            // only the interlock, so Mox alone would apply changes mid-over
            // for a VOX operator. FlexBase hands the same question to the
            // conditioner itself when the transmit stream opens; the chain
            // asks it on the control side to say whether a change waits.
            cond.KeySource = rig.TransmitKeySource;
            chain.KeySource = rig.TransmitKeySource;
            cond.NoiseReducer = NrStage;
            cond.NoiseReducerAppliedLatencySamples = AppliedNrLatency;
            cond.MonitorSink = MonitorStage;
            PushGate(cond.Gate);
            cond.AutoThresholdInForce = _autoThreshold;
            cond.ThresholdMarginInForceDb = _thresholdMarginDb;

            rig.ModeChanged += OnModeChanged;
            ApplyModePolicy(rig.Mode ?? "");

            // Twice a second: refresh the derived threshold from the live
            // noise-floor estimate. Cheap — the profile is cached against
            // the meter's block count and only recomputes while transmitting,
            // which is exactly when the floor estimate is improving.
            _policyTimer = new System.Timers.Timer(500) { AutoReset = true };
            _policyTimer.Elapsed += (s, e) =>
            {
                RefreshDerivedThreshold();
                // R3's backstop: held changes run once unkeyed, even through an
                // over in CW, where the noise chain is stood aside and never
                // sees the unkey.
                _chain?.ApplyDeferred();
            };
            _policyTimer.Start();

            Trace.WriteLine("TxAudioConditioning: attached");
        }

        /// <summary>Unhook from the rig and release the monitor device. The noise chain and the gate settings stay, as the operator's intent.</summary>
        public static void Detach()
        {
            var rig = _rig;
            _rig = null;
            if (rig != null)
            {
                rig.ModeChanged -= OnModeChanged;
                var cond = rig.TxConditioner;
                cond.NoiseReducer = null;
                cond.NoiseReducerAppliedLatencySamples = null;
                cond.MonitorSink = null;
                cond.MonitorMode = JJPortaudio.TxAudioConditioner.MonitorModes.Off;
            }
            var chain = _chain;
            if (chain != null) chain.KeySource = null;
            _policyTimer?.Stop();
            _policyTimer?.Dispose();
            _policyTimer = null;
            _player?.Dispose();
            _player = null;
        }

        // ------------------------------------------------------------------
        // Stages (audio callback thread)
        // ------------------------------------------------------------------

        private static void NrStage(float[] buffer, int count, uint sampleRate)
        {
            _nrAppliedLatency = 0;
            var chain = _chain;
            if (chain == null) return;
            // Every engine is 48 kHz-native (480-sample frames are 10 ms only
            // there). The chain passes any other rate through honestly, and
            // honestly means the residual goes silent, which is the designed
            // tell that the stage is not live.
            chain.Process(buffer, count, sampleRate, _rig?.TxConditioner.Keyed ?? false);
            _nrAppliedLatency = chain.AppliedLatencySamples;
        }

        private static int AppliedNrLatency() => _nrAppliedLatency;

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
            // The policy IN FORCE on the chain, not the intent: a whole setup
            // that is still waiting for the unkey has not changed it (repair
            // round 3). The tick itself stays live while keyed (S15, pending).
            var cond = rig?.TxConditioner;
            if (cond == null || !cond.AutoThresholdInForce) return;
            try
            {
                var profile = rig.TxLoudnessProfile;
                if (!profile.IsValid) return;
                if (profile.NoiseFloorLufs <= JJPortaudio.LufsMeter.Floor) return;
                cond.Gate.ThresholdDb =
                    profile.NoiseFloorLufs + cond.ThresholdMarginInForceDb;
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
        }

        /// <summary>
        /// The transmit chain's mode policy applies before its noise reducer.
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

        /// <summary>
        /// The neural slot's on or off, the switch the Audio Workshop has always
        /// called transmit noise reduction. The operator's intent: held with no
        /// radio (#1077), adopted at the next unkey when keyed (item B). Use
        /// <see cref="TxNoiseChain.SetEnabled"/> to learn when it takes effect.
        /// </summary>
        public static bool NrEnabled
        {
            get { return Chain.WantsEnabled(DenoiseFamily.Neural); }
            set { Chain.SetEnabled(DenoiseFamily.Neural, value); }
        }

        /// <summary>
        /// The neural slot's chosen engine's strength, 0..1 wet/dry, stored as
        /// it always was; the operator meets it as a noise floor (#1019,
        /// <see cref="NoiseFloor"/>). LIVE while keyed and while monitoring —
        /// hear your own voice in the residual, raise the floor, hear it gone.
        /// That loop is the whole point of the residual monitor.
        /// </summary>
        public static float NrStrength
        {
            get { var c = Chain; return c.GetStrength(c.ChosenEngineId(DenoiseFamily.Neural)); }
            set { var c = Chain; c.SetStrength(c.ChosenEngineId(DenoiseFamily.Neural), Math.Clamp(value, 0f, 1f)); }
        }

        /// <summary>Transmit RNNoise's stored default (floor 20, ruled S3), from the one source.</summary>
        internal static float DefaultNrStrength => NoiseFloor.DefaultStrength("rnnoise", DenoiseDirection.Transmit);

        /// <summary>Transmit noise gate on/off. Held with no radio (#1077).</summary>
        public static bool GateEnabled
        {
            get { lock (_gate) return _gateIntent.Enabled; }
            set { SetGateEnabled(value); }
        }

        /// <summary>
        /// Turn the gate on or off (repair round 1, R3): like an engine change,
        /// it waits for the unkey when keyed, and the returned timing says so;
        /// held with no radio (#1077). Unkeyed, the conditioner still adopts it
        /// only on an unkeyed buffer.
        /// </summary>
        public static TxChangeResult SetGateEnabled(bool on) => Chain.RunOrDefer(() => SetGateEnabledNow(on));

        private static void SetGateEnabledNow(bool on)
        {
            lock (_gate) _gateIntent.Enabled = on;
            Conditioner?.RequestGateEnabled(on);
        }

        /// <summary>A continuous gate lever: live or held for the unkey by the one policy, <see cref="TxNoiseChain.ContinuousLeversLiveWhileKeyed"/>.</summary>
        private static void GateLever(Action<TxNoiseGateSetup> intent, Action<JJPortaudio.TxNoiseGate> live,
            Action<JJPortaudio.TxAudioConditioner.StagedGate> staged) =>
            Chain.Continuous(() =>
            {
                lock (_gate) intent(_gateIntent);
                var c = Conditioner;
                if (c == null) return;
                live(c.Gate);
                // A whole setup's gate still waiting: the later lone change wins there too.
                c.ChangeStagedGate(staged);
            });

        /// <summary>
        /// Margin above the measured noise floor for the derived threshold,
        /// dB (6..10 recommended; clamped 3..15).
        /// </summary>
        public static float ThresholdMarginDb
        {
            get { return _thresholdMarginDb; }
            set
            {
                float v = Math.Clamp(value, 3f, 15f);
                Chain.Continuous(() =>
                {
                    _thresholdMarginDb = v;
                    var c = Conditioner;
                    if (c == null) return;
                    c.ThresholdMarginInForceDb = v;
                    c.ChangeStagedGate(g => g.ThresholdMarginDb = v);
                });
            }
        }

        /// <summary>Derive the threshold from the measured floor (default) or
        /// leave it wherever the operator set it by hand.</summary>
        public static bool AutoThreshold
        {
            get { return _autoThreshold; }
            set
            {
                // Live, outside the continuous-lever policy: S15 asks about it.
                _autoThreshold = value;
                var c = Conditioner;
                if (c == null) return;
                c.AutoThresholdInForce = value;
                c.ChangeStagedGate(g => { g.AutoThreshold = value; g.HasThreshold = !value; });
            }
        }

        /// <summary>The gate's threshold by hand, dB (used when <see cref="AutoThreshold"/> is off). Held with no radio.</summary>
        public static float GateThresholdDb
        {
            get { var c = Conditioner; if (c != null) return c.Gate.ThresholdDb; lock (_gate) return _gateIntent.ThresholdDb; }
            set { GateLever(g => g.ThresholdDb = value, g => g.ThresholdDb = value, g => g.ThresholdDb = value); }
        }

        /// <summary>Gate attack, ms. Held with no radio.</summary>
        public static float GateAttackMs
        {
            get { lock (_gate) return _gateIntent.AttackMs; }
            set { GateLever(g => g.AttackMs = value, g => g.AttackMs = value, g => g.AttackMs = value); }
        }

        /// <summary>Gate hold, ms. Held with no radio.</summary>
        public static float GateHoldMs
        {
            get { lock (_gate) return _gateIntent.HoldMs; }
            set { GateLever(g => g.HoldMs = value, g => g.HoldMs = value, g => g.HoldMs = value); }
        }

        /// <summary>Gate release, ms. Held with no radio.</summary>
        public static float GateReleaseMs
        {
            get { lock (_gate) return _gateIntent.ReleaseMs; }
            set { GateLever(g => g.ReleaseMs = value, g => g.ReleaseMs = value, g => g.ReleaseMs = value); }
        }

        /// <summary>Gate depth, dB. Held with no radio.</summary>
        public static float GateRangeDb
        {
            get { lock (_gate) return _gateIntent.RangeDb; }
            set { GateLever(g => g.RangeDb = value, g => g.RangeDb = value, g => g.RangeDb = value); }
        }

        /// <summary>Copy the held gate settings onto a rig's gate (attach, and after applying a setup).</summary>
        private static void PushGate(JJPortaudio.TxNoiseGate gate)
        {
            TxNoiseGateSetup g;
            lock (_gate) g = _gateIntent.Clone();
            gate.Enabled = g.Enabled;
            if (!_autoThreshold) gate.ThresholdDb = g.ThresholdDb;
            gate.AttackMs = g.AttackMs;
            gate.HoldMs = g.HoldMs;
            gate.ReleaseMs = g.ReleaseMs;
            gate.RangeDb = g.RangeDb;
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

        // ------------------------------------------------------------------
        // Setups (item F)
        // ------------------------------------------------------------------

        /// <summary>The live state as a setup: both slots as the operator chose them, and the gate.</summary>
        public static TxNoiseSetup CaptureSetup(string name)
        {
            var (spectral, neural) = Chain.CaptureSlots();
            TxNoiseGateSetup gate;
            lock (_gate) gate = _gateIntent.Clone();
            gate.AutoThreshold = _autoThreshold;
            gate.ThresholdMarginDb = _thresholdMarginDb;
            var live = Conditioner?.Gate;
            if (live != null) gate.ThresholdDb = live.ThresholdDb;
            return new TxNoiseSetup { Name = name ?? "", Spectral = spectral, Neural = neural, Gate = gate };
        }

        /// <summary>
        /// Put a setup in place: both slots (their engine and on-or-off changes
        /// wait for the unkey when keyed, item B; the returned timing says
        /// which) and the gate (live).
        /// </summary>
        public static TxChangeResult ApplySetup(TxNoiseSetup setup)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            // A copy now: the caller's object may change before the unkey.
            var copy = setup.Clone();
            return Chain.RunOrDefer(() => ApplySetupNow(copy));
        }

        /// <summary>
        /// A whole setup, now, as ONE change (repair round 1, R3: a setup chosen
        /// or a microphone switched while keyed waits for the unkey entirely,
        /// gate included). The slots are replaced, not overlaid (R2).
        /// </summary>
        private static void ApplySetupNow(TxNoiseSetup setup)
        {
            Chain.ApplySlotsNow(setup.Spectral, setup.Neural);
            ApplyGateNow(setup.Gate);
        }

        /// <summary>
        /// The gate half of a setup, now, on the control side (Sprint 49 Track
        /// H2): for a caller that holds a whole setup or preset as ONE change
        /// through <see cref="TxNoiseChain.RunOrDefer"/> and must not let the
        /// gate's own on and off be deferred a second time inside it, or put
        /// behind changes asked for after it.
        /// </summary>
        internal static void ApplyGateNow(TxNoiseGateSetup gate)
        {
            var g = (gate ?? new TxNoiseGateSetup()).Clone();
            _autoThreshold = g.AutoThreshold;
            _thresholdMarginDb = Math.Clamp(g.ThresholdMarginDb, 3f, 15f);
            lock (_gate) _gateIntent = g;
            // The gate as a whole, on or off, levels and threshold policy, staged
            // for the next unkeyed buffer (repair round 3): never partway through
            // an over, even if this call is running when a key-down arrives.
            Conditioner?.StageGate(new JJPortaudio.TxAudioConditioner.StagedGate
            {
                Enabled = g.Enabled,
                HasThreshold = !g.AutoThreshold,
                ThresholdDb = g.ThresholdDb,
                AttackMs = g.AttackMs,
                HoldMs = g.HoldMs,
                ReleaseMs = g.ReleaseMs,
                RangeDb = g.RangeDb,
                AutoThreshold = g.AutoThreshold,
                ThresholdMarginDb = Math.Clamp(g.ThresholdMarginDb, 3f, 15f),
            });
        }

        private static readonly HashSet<string> _missingSaid = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Apply the setup a microphone names on a radio. A name this radio does
        /// not hold applies NOTHING and keeps the chain as it was (ruled
        /// 2026-11-07 07:52); <see cref="TxSetupApplyResult.FirstTime"/> is true
        /// the first time a given radio and name are found missing this session,
        /// so the caller says it once. A name found again after being missing
        /// counts afresh.
        /// </summary>
        public static TxSetupApplyResult ApplyNamedSetup(string radioId, string name, TxNoiseSetupStore store = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return new(TxSetupApplyOutcome.NoName);
            store ??= TxNoiseSetupStore.Default();
            if (store == null) return new(TxSetupApplyOutcome.NoName);
            string key = (radioId ?? "") + "|" + name.Trim();
            var setup = store.Find(radioId, name);
            if (setup == null)
            {
                bool first;
                lock (_missingSaid) first = _missingSaid.Add(key);
                Trace.WriteLine($"TxAudioConditioning: radio {radioId} has no transmit noise setup called '{name}'; nothing applied, the last values stay.");
                return new(TxSetupApplyOutcome.Missing, first);
            }
            lock (_missingSaid) _missingSaid.Remove(key);
            var timing = ApplySetup(setup).Timing;
            return new(TxSetupApplyOutcome.Applied, false, timing);
        }

        /// <summary>The old settings shape, for code that still reads it: the neural slot and the gate.</summary>
        public static TxConditioningSettings CaptureSettings() => TxNoiseSetupMigration.ToConditioning(CaptureSetup(""));

        /// <summary>Apply the old settings shape: transmit RNNoise on the neural slot, and the gate.</summary>
        public static void ApplySettings(TxConditioningSettings s)
        {
            if (s == null) return;
            ApplySetup(TxNoiseSetupMigration.FromConditioning("", s));
        }

        /// <summary>Back to the recommended state: RNNoise at its default floor, off; spectral off; the gate as shipped, its threshold derived from the floor again.</summary>
        public static TxChangeResult ResetToRecommended() => Chain.RunOrDefer(() =>
        {
            // The recommended setup replaces both slots (spectral off, RNNoise
            // at its default floor and off) and the gate, as one change.
            ApplySetupNow(TxNoiseSetupMigration.FromConditioning("", new TxConditioningSettings()));
            var gate = Conditioner?.Gate;
            if (gate != null) gate.ThresholdDb = JJPortaudio.TxNoiseGate.DefaultThresholdDb;
            lock (_gate) _gateIntent.ThresholdDb = JJPortaudio.TxNoiseGate.DefaultThresholdDb;
            RefreshDerivedThreshold();
        });
    }

    // TxConditioningSettings moved to Radios\MicrophoneProfile.cs
    // (Track B, 2026-08-18, #44): it is the microphone profile's payload
    // now, exactly as the note it carried here always said it should be.
}
