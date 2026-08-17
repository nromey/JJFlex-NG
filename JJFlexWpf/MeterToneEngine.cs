using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using NAudio.Wave.SampleProviders;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// Real-time meter sonification engine. Subscribes to FlexBase meter events
    /// and drives VoicedToneSampleProvider instances to render meter values as
    /// continuous pitched tones.
    ///
    /// Track D2 rework: each slot wraps a <see cref="MeterDefinition"/> —
    /// source plus range plus voice — and the synthesis is data-driven through
    /// <see cref="MeterVoice"/>. The grammar: timbre identifies the meter,
    /// pitch carries its value, pan enhances but is never load-bearing.
    /// Includes a Peak Watcher for TX safety alerts and on-demand speech
    /// readout of all active meters.
    /// </summary>
    public static class MeterToneEngine
    {
        private static FlexBase? _rig;
        private static bool _initialized;

        // Per-slot throttle interval: 100ms = 10 Hz update rate. Throttling is
        // per slot (not global) so four active meters each update at 10 Hz
        // instead of sharing one 10 Hz budget between them.
        private const long ThrottleIntervalTicks = TimeSpan.TicksPerMillisecond * 100;

        /// <summary>Global kill switch for all meter tones.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (!value)
                {
                    foreach (var slot in Slots)
                        slot.ToneProvider.Active = false;
                }
                else if (_rig != null)
                {
                    // Proactively activate slots so tones start immediately
                    // rather than waiting for the next OnMeterChanged event.
                    bool tx = _rig.Transmit;
                    foreach (var slot in Slots)
                    {
                        if (!slot.Enabled) continue;
                        slot.ToneProvider.Active = ShouldSound(slot.Definition.Activation, tx);
                    }
                }
            }
        }
        private static bool _enabled;

        /// <summary>Whether speech readout of meter values is enabled.</summary>
        public static bool SpeechEnabled { get; set; } = true;

        /// <summary>Speech interval in seconds (1-10). How often batched meter values are spoken.</summary>
        public static int SpeechIntervalSeconds { get; set; } = 3;

        /// <summary>Whether the speech timer is actively speaking meter values.</summary>
        public static bool SpeechTimerActive
        {
            get => _speechTimerActive;
            set
            {
                _speechTimerActive = value;
                if (value) StartSpeechTimer();
                else StopSpeechTimer();
            }
        }
        private static bool _speechTimerActive;
        private static System.Windows.Threading.DispatcherTimer? _speechTimer;

        /// <summary>When true, enables default meters when TxTune activates.</summary>
        public static bool AutoEnableOnTune { get; set; }

        /// <summary>Master volume multiplier for all meter tones (0.0–1.0).</summary>
        public static float MasterVolume { get; set; } = 0.5f;

        /// <summary>The meter tone slots.</summary>
        public static List<MeterSlot> Slots { get; } = new();

        /// <summary>Current preset name.</summary>
        public static string CurrentPreset { get; private set; } = "RX Monitor";

        private static readonly string[] PresetNames = { "RX Monitor", "TX Monitor", "Full Monitor" };
        private static int _presetIndex;

        // Peak Watcher state
        public static bool PeakWatcherEnabled { get; set; } = true;
        private static long _lastPeakWarningTicks;
        private static long _alcHighStartTicks;
        private static bool _alcSustainedWarning;
        private const long PeakCooldownTicks = TimeSpan.TicksPerSecond * 10;
        private const long AlcSustainedThresholdTicks = TimeSpan.TicksPerSecond * 3;
        private const float AlcWarningThreshold = 0.5f;
        private const float AlcCriticalThreshold = 0.8f;

        /// <summary>
        /// Initialize the engine and create the default tone slots.
        /// Call after EarconPlayer.Initialize().
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            for (int i = 0; i < 4; i++)
            {
                var slot = new MeterSlot();
                Slots.Add(slot);
                EarconPlayer.RegisterContinuousStereo(slot.ToneProvider);
            }

            ApplyPreset("RX Monitor");
            _initialized = true;
        }

        /// <summary>
        /// Wire the engine to a connected radio's meter data.
        /// </summary>
        public static void AttachToRadio(FlexBase rig)
        {
            if (_rig != null)
                DetachFromRadio();

            _rig = rig;
            _rig.MeterChanged += OnMeterChanged;
            _rig.TransmitChange += OnTransmitChanged;
        }

        /// <summary>Disconnect from the current radio.</summary>
        public static void DetachFromRadio()
        {
            if (_rig != null)
            {
                _rig.MeterChanged -= OnMeterChanged;
                _rig.TransmitChange -= OnTransmitChanged;
                _rig = null;
            }
            // Silence all tones
            foreach (var slot in Slots)
                slot.ToneProvider.Active = false;
        }

        /// <summary>Shut down the engine and clean up.</summary>
        public static void Shutdown()
        {
            DetachFromRadio();
            EarconPlayer.UnregisterAllContinuousTones();
            Slots.Clear();
            _initialized = false;
        }

        #region Presets

        /// <summary>
        /// Apply a named preset configuration. Replaces the working set — the
        /// historical semantics, kept. Voice assignments per meter are chosen
        /// so identities differ on timbre AND modulation, never on pan alone:
        /// pan is an enhancement, lost entirely to mono listeners and
        /// asymmetric hearing loss.
        /// </summary>
        public static void ApplyPreset(string presetName)
        {
            // Silence all first
            foreach (var slot in Slots)
            {
                slot.Enabled = false;
                slot.ToneProvider.Active = false;
            }

            CurrentPreset = presetName;
            _presetIndex = Array.IndexOf(PresetNames, presetName);
            if (_presetIndex < 0) _presetIndex = 0;

            switch (presetName)
            {
                case "RX Monitor":
                    ConfigureSlot(0, "SMeter", true, 0.6f, 0f, 200, 1200, "Pure");
                    ConfigureSlot(1, "SWR", false, 0.5f, 0f, 200, 1200, "Trill");
                    ConfigureSlot(2, "ALC", false, 0.5f, 0f, 300, 1500, "Raspy");
                    ConfigureSlot(3, "Mic", false, 0.4f, 0f, 350, 800, "Hollow");
                    break;
                case "TX Monitor":
                    ConfigureSlot(0, "ALC", true, 0.5f, -0.5f, 300, 1500, "Raspy");
                    ConfigureSlot(1, "Mic", true, 0.4f, 0.5f, 350, 800, "Hollow");
                    ConfigureSlot(2, "Power", true, 0.4f, 0f, 200, 1000, "Organ");
                    ConfigureSlot(3, "SWR", true, 0.5f, 0f, 200, 1200, "Trill");
                    break;
                case "Full Monitor":
                    ConfigureSlot(0, "SMeter", true, 0.5f, -0.5f, 200, 1200, "Pure");
                    ConfigureSlot(1, "ALC", true, 0.4f, 0.5f, 300, 1500, "Raspy");
                    ConfigureSlot(2, "SWR", true, 0.5f, 0f, 200, 1200, "Trill");
                    ConfigureSlot(3, "Mic", true, 0.3f, 0f, 350, 800, "Hollow");
                    break;
            }
        }

        /// <summary>Cycle to the next preset.</summary>
        public static void CyclePreset()
        {
            _presetIndex = (_presetIndex + 1) % PresetNames.Length;
            ApplyPreset(PresetNames[_presetIndex]);
        }

        private static void ConfigureSlot(int index, string sourceKey, bool enabled,
            float volume, float pan, float pitchLow, float pitchHigh, string voiceName)
        {
            if (index >= Slots.Count) return;
            var slot = Slots[index];
            var def = LegacyMeterCatalog.CreateDefinition(sourceKey);
            def.Enabled = enabled;
            def.Volume = volume;
            def.Pan = pan;
            def.PitchLowHz = pitchLow;
            def.PitchHighHz = pitchHigh;
            def.VoiceName = voiceName;
            slot.SetDefinition(def);
        }

        #endregion

        #region Definition round-trip (persistence)

        /// <summary>
        /// Replace the working set with saved meter definitions (the one meter
        /// list from AudioOutputConfig.Meters). Slots are created or removed
        /// to match, capped at <see cref="MaxSlots"/>.
        /// </summary>
        public static void LoadDefinitions(IEnumerable<MeterDefinition> definitions)
        {
            var defs = definitions.Take(MaxSlots).Select(d => d.Clone()).ToList();
            if (defs.Count == 0) return;

            // Shrink or grow the slot list to fit.
            while (Slots.Count > Math.Max(defs.Count, 1))
            {
                var last = Slots[^1];
                last.ToneProvider.Active = false;
                EarconPlayer.UnregisterContinuousTone(last.ToneProvider);
                Slots.RemoveAt(Slots.Count - 1);
            }
            while (Slots.Count < defs.Count)
            {
                var slot = new MeterSlot();
                Slots.Add(slot);
                EarconPlayer.RegisterContinuousStereo(slot.ToneProvider);
            }

            for (int i = 0; i < defs.Count; i++)
                Slots[i].SetDefinition(defs[i]);
        }

        /// <summary>Snapshot the working set for persistence.</summary>
        public static List<MeterDefinition> ExportDefinitions() =>
            Slots.Select(s => s.Definition.Clone()).ToList();

        #endregion

        #region Dynamic Slot Management

        /// <summary>Maximum number of meter tone slots.</summary>
        public const int MaxSlots = 8;

        /// <summary>Add a new meter slot. Returns the slot, or null if at max.</summary>
        public static MeterSlot? AddSlot()
        {
            if (Slots.Count >= MaxSlots) return null;
            var slot = new MeterSlot();
            Slots.Add(slot);
            EarconPlayer.RegisterContinuousStereo(slot.ToneProvider);
            return slot;
        }

        /// <summary>Remove a slot by index. Cannot remove if only 1 slot remains.</summary>
        public static bool RemoveSlot(int index)
        {
            if (Slots.Count <= 1 || index < 0 || index >= Slots.Count) return false;
            var slot = Slots[index];
            slot.ToneProvider.Active = false;
            EarconPlayer.UnregisterContinuousTone(slot.ToneProvider);
            Slots.RemoveAt(index);
            return true;
        }

        #endregion

        #region Auto-Enable on Tune

        private static bool _wasEnabledBeforeTune;

        /// <summary>
        /// Call when TxTune is toggled on. If AutoEnableOnTune is set,
        /// enables meters with current config.
        /// </summary>
        public static void OnTuneStarted()
        {
            if (!AutoEnableOnTune) return;
            _wasEnabledBeforeTune = _enabled;
            if (!_enabled) Enabled = true;
        }

        /// <summary>
        /// Call when TxTune is toggled off. Restores previous meter state.
        /// </summary>
        public static void OnTuneStopped()
        {
            if (!AutoEnableOnTune) return;
            if (!_wasEnabledBeforeTune) Enabled = false;
        }

        #endregion

        #region Speech Timer

        private static void StartSpeechTimer()
        {
            if (_speechTimer != null) return;
            _speechTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(SpeechIntervalSeconds)
            };
            _speechTimer.Tick += (s, e) =>
            {
                if (!_speechTimerActive || !SpeechEnabled) return;
                SpeakMeters();
            };
            _speechTimer.Start();
        }

        private static void StopSpeechTimer()
        {
            _speechTimer?.Stop();
            _speechTimer = null;
        }

        /// <summary>Update the speech timer interval (call when SpeechIntervalSeconds changes).</summary>
        public static void UpdateSpeechTimerInterval()
        {
            if (_speechTimer != null)
            {
                _speechTimer.Interval = TimeSpan.FromSeconds(SpeechIntervalSeconds);
            }
        }

        #endregion

        #region Meter Event Handling

        private static void OnMeterChanged(object sender, MeterType meter, float value)
        {
            long now = DateTime.UtcNow.Ticks;

            // Peak Watcher runs regardless of tone throttling — it has its own
            // cooldown and a safety alert should never wait behind a tone.
            if (_enabled && PeakWatcherEnabled && _rig != null && _rig.Transmit
                && meter == MeterType.ALC)
            {
                CheckPeakWatcher(value, now);
            }

            if (!_enabled || _rig == null) return;

            bool transmitting = _rig.Transmit;
            string key = meter.ToString();

            // Update each slot whose source matches this meter. More than one
            // may match — two meters sharing a source (coarse and fine SWR) is
            // an explicitly supported shape.
            foreach (var slot in Slots)
            {
                var def = slot.Definition;
                if (!def.Enabled) continue;
                if (!SourceMatchesLegacy(def.Source, key)) continue;

                // Per-slot throttle to ~10 Hz to avoid audio glitching.
                if (now - slot.LastUpdateTicks < ThrottleIntervalTicks) continue;
                slot.LastUpdateTicks = now;

                bool shouldSound = ShouldSound(def.Activation, transmitting);
                slot.ToneProvider.Active = shouldSound;

                if (shouldSound)
                {
                    slot.ToneProvider.Frequency = def.PitchForValue(value);
                    slot.ToneProvider.Volume = def.Volume * MasterVolume;
                    slot.ToneProvider.Pan = def.Pan;
                    slot.ToneProvider.Voice = def.EffectiveVoice();
                }
            }
        }

        private static void OnTransmitChanged(object sender, bool transmitting)
        {
            if (!_enabled) return;

            // When TX state changes, update which slots are active
            foreach (var slot in Slots)
            {
                if (!slot.Enabled) continue;
                slot.ToneProvider.Active = ShouldSound(slot.Definition.Activation, transmitting);
            }

            // Reset peak watcher state on TX→RX transition
            if (!transmitting)
            {
                _alcHighStartTicks = 0;
                _alcSustainedWarning = false;
            }
        }

        /// <summary>Does a source reference match a legacy FlexBase meter
        /// event? Only radio-reported sources with legacy keys are live until
        /// the real meter-list accessor lands (Track B).</summary>
        private static bool SourceMatchesLegacy(MeterSourceRef source, string legacyKey) =>
            source.Kind == MeterSourceKind.RadioReported &&
            string.Equals(source.Key, legacyKey, StringComparison.OrdinalIgnoreCase);

        private static bool ShouldSound(MeterActivation activation, bool transmitting) =>
            activation switch
            {
                MeterActivation.ReceiveOnly => !transmitting,
                MeterActivation.TransmitOnly => transmitting,
                _ => true,
            };

        #endregion

        #region Peak Watcher

        private static void CheckPeakWatcher(float alcValue, long nowTicks)
        {
            // Cooldown check
            if (nowTicks - _lastPeakWarningTicks < PeakCooldownTicks) return;

            if (alcValue > AlcCriticalThreshold)
            {
                // Critical: immediate alert
                _lastPeakWarningTicks = nowTicks;
                try { EarconPlayer.Warning2Beep(); } catch { }
                if (SpeechEnabled)
                    ScreenReaderOutput.Speak("ALC high", VerbosityLevel.Critical);
            }
            else if (alcValue > AlcWarningThreshold)
            {
                // Warning: alert after 3 seconds sustained
                if (_alcHighStartTicks == 0)
                {
                    _alcHighStartTicks = nowTicks;
                }
                else if (!_alcSustainedWarning &&
                         nowTicks - _alcHighStartTicks > AlcSustainedThresholdTicks)
                {
                    _alcSustainedWarning = true;
                    _lastPeakWarningTicks = nowTicks;
                    try { EarconPlayer.Warning1Beep(); } catch { }
                    if (SpeechEnabled)
                        ScreenReaderOutput.Speak("ALC warning", VerbosityLevel.Critical);
                }
            }
            else
            {
                // Below threshold — reset
                _alcHighStartTicks = 0;
                _alcSustainedWarning = false;
            }
        }

        #endregion

        #region Speech Readout

        /// <summary>
        /// Generate a speech summary of current meter values.
        /// Works whether tones are on or off.
        /// </summary>
        public static string GetMeterSpeechSummary()
        {
            if (_rig == null) return "No radio connected";

            var sb = new StringBuilder();
            bool tx = _rig.Transmit;

            if (!tx)
            {
                // RX meters
                int sUnits = _rig.SMeter;
                if (sUnits <= 9)
                    sb.Append($"S-meter S{sUnits}. ");
                else
                    sb.Append($"S-meter S9 plus {(sUnits - 9) * 6}. ");
            }
            else
            {
                // TX meters
                float powerDbm = _rig.PowerDBM;
                int watts = (int)((Math.Pow(10.0, powerDbm / 10.0) / 1000.0) + 0.5);
                sb.Append($"Forward power {watts} watts. ");

                float swr = _rig.SWRValue;
                if (swr > 0)
                    sb.Append($"SWR {swr:F1}. ");

                float alc = _rig.ALC;
                if (alc > 0.01f)
                    sb.Append($"ALC {alc:F2}. ");

                float mic = _rig.MicData;
                sb.Append($"Mic {mic:F1} dB. ");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>Speak the current meter summary via screen reader.</summary>
        public static void SpeakMeters()
        {
            string summary = GetMeterSpeechSummary();
            ScreenReaderOutput.Speak(summary, VerbosityLevel.Terse, true);
        }

        #endregion
    }

    /// <summary>
    /// Legacy meter sources. Retained as a bridge: the model's source space is
    /// <see cref="MeterSourceRef"/> (which also expresses PC-derived,
    /// frequency-domain and derived sources); this enum names the eight
    /// FlexBase event sources that are live today, and existing UI indexes it.
    /// </summary>
    public enum MeterSource
    {
        SMeter, ALC, Mic, Power, SWR, Compression, Voltage, PATemp
    }

    /// <summary>
    /// A meter tone slot: a <see cref="MeterDefinition"/> (source + range +
    /// voice + mapping) bound to a live <see cref="VoicedToneSampleProvider"/>.
    /// The legacy flat properties (Source, Waveform, PitchLow…) are bridges
    /// over the definition so existing callers keep working; new code should
    /// use <see cref="Definition"/> directly.
    /// </summary>
    public class MeterSlot
    {
        /// <summary>The model object this slot renders.</summary>
        public MeterDefinition Definition { get; private set; }

        /// <summary>The live synthesis provider (stereo, live pan).</summary>
        public VoicedToneSampleProvider ToneProvider { get; } = new();

        /// <summary>Per-slot tone update throttle bookkeeping.</summary>
        internal long LastUpdateTicks;

        public MeterSlot()
        {
            Definition = LegacyMeterCatalog.CreateDefinition("SMeter");
            SyncProvider();
        }

        /// <summary>Replace the definition wholesale and resync the provider.</summary>
        public void SetDefinition(MeterDefinition definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            SyncProvider();
        }

        private void SyncProvider()
        {
            ToneProvider.Voice = Definition.EffectiveVoice();
            ToneProvider.Pan = Definition.Pan;
        }

        // ---- legacy bridges ----

        /// <summary>Legacy source bridge. Setting it rebinds the definition to
        /// that catalog source, refreshing range, units and activation.</summary>
        public MeterSource Source
        {
            get => Enum.TryParse<MeterSource>(Definition.Source.Key, true, out var s)
                ? s : MeterSource.SMeter;
            set
            {
                var entry = LegacyMeterCatalog.Find(value.ToString());
                if (entry == null) return;
                Definition.Name = entry.DisplayName;
                Definition.Source = new MeterSourceRef
                {
                    Kind = MeterSourceKind.RadioReported,
                    Key = entry.Key,
                };
                Definition.Range = new MeterRange
                {
                    Low = entry.Low,
                    High = entry.High,
                    Units = entry.Units,
                    UnitsLabel = entry.UnitsLabel,
                };
                Definition.Activation = entry.Activation;
            }
        }

        public bool Enabled
        {
            get => Definition.Enabled;
            set => Definition.Enabled = value;
        }

        public float Volume
        {
            get => Definition.Volume;
            set
            {
                Definition.Volume = value;
                ToneProvider.Volume = value * MeterToneEngine.MasterVolume;
            }
        }

        /// <summary>Pan, -1..+1. Live: takes effect on the next audio buffer
        /// (the old engine's pan was fixed at mixer registration and changing
        /// it silently did nothing).</summary>
        public float Pan
        {
            get => Definition.Pan;
            set
            {
                Definition.Pan = value;
                ToneProvider.Pan = value;
            }
        }

        public int PitchLow
        {
            get => (int)Definition.PitchLowHz;
            set => Definition.PitchLowHz = value;
        }

        public int PitchHigh
        {
            get => (int)Definition.PitchHighHz;
            set => Definition.PitchHighHz = value;
        }

        /// <summary>The voice this meter speaks with. Setting it clears any
        /// live-tweak override — an explicit voice choice replaces the tweak.</summary>
        public string VoiceName
        {
            get => Definition.VoiceName;
            set
            {
                Definition.VoiceName = value;
                Definition.VoiceOverride = null;
                SyncProvider();
            }
        }

        /// <summary>Legacy waveform bridge: maps the old enum onto the
        /// equivalent built-in voice. Getter answers Sine for anything the old
        /// enum cannot express.</summary>
        public WaveformType Waveform
        {
            get => Definition.VoiceName switch
            {
                "Square" => WaveformType.Square,
                "Reedy" => WaveformType.Sawtooth,
                "Pulsing" => WaveformType.SlowPulse,
                "Urgent" => WaveformType.FastPulse,
                "Ring" => WaveformType.Alternating,
                _ => WaveformType.Sine,
            };
            set => VoiceName = MeterVoiceLibrary.FromLegacyWaveform(value);
        }
    }
}
