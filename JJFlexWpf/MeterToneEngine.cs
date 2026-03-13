using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NAudio.Wave.SampleProviders;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// Real-time meter sonification engine. Subscribes to FlexBase meter events
    /// and drives ContinuousToneSampleProvider instances to render S-meter, ALC,
    /// mic level, and SWR as continuous pitched tones.
    ///
    /// Up to 4 simultaneous meter tone slots, each with independent frequency
    /// mapping, volume, and stereo panning. Includes a Peak Watcher for TX safety
    /// alerts and on-demand speech readout of all active meters.
    /// </summary>
    public static class MeterToneEngine
    {
        private static FlexBase? _rig;
        private static bool _initialized;
        private static long _lastUpdateTicks; // For throttling to ~10 Hz

        // Throttle interval: 100ms = 10 Hz update rate
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
                        slot.ToneProvider.Active = ShouldSlotSound(slot.Source, tx);
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

        /// <summary>The 4 meter tone slots.</summary>
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
        /// Initialize the engine and create the 4 tone slots.
        /// Call after EarconPlayer.Initialize().
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            // Create 4 slots with default configurations
            for (int i = 0; i < 4; i++)
            {
                var slot = new MeterSlot();
                Slots.Add(slot);
            }

            // Register all tone providers with the EarconPlayer mixer
            foreach (var slot in Slots)
            {
                EarconPlayer.RegisterContinuousTone(slot.ToneProvider, slot.Pan);
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

        /// <summary>Apply a named preset configuration.</summary>
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
                    ConfigureSlot(0, MeterSource.SMeter, true, 0.6f, 0f, 200, 1200);
                    ConfigureSlot(1, MeterSource.SWR, false, 0.5f, 0f, 200, 1200);
                    ConfigureSlot(2, MeterSource.ALC, false, 0.5f, 0f, 300, 1500);
                    ConfigureSlot(3, MeterSource.Mic, false, 0.4f, 0f, 350, 800);
                    break;
                case "TX Monitor":
                    ConfigureSlot(0, MeterSource.ALC, true, 0.5f, -0.5f, 300, 1500);
                    ConfigureSlot(1, MeterSource.Mic, true, 0.4f, 0.5f, 350, 800);
                    ConfigureSlot(2, MeterSource.Power, true, 0.4f, 0f, 200, 1000);
                    ConfigureSlot(3, MeterSource.SWR, true, 0.5f, 0f, 200, 1200);
                    break;
                case "Full Monitor":
                    ConfigureSlot(0, MeterSource.SMeter, true, 0.5f, -0.5f, 200, 1200);
                    ConfigureSlot(1, MeterSource.ALC, true, 0.4f, 0.5f, 300, 1500);
                    ConfigureSlot(2, MeterSource.SWR, true, 0.5f, 0f, 200, 1200);
                    ConfigureSlot(3, MeterSource.Mic, true, 0.3f, 0f, 350, 800);
                    break;
            }
        }

        /// <summary>Cycle to the next preset.</summary>
        public static void CyclePreset()
        {
            _presetIndex = (_presetIndex + 1) % PresetNames.Length;
            ApplyPreset(PresetNames[_presetIndex]);
        }

        private static void ConfigureSlot(int index, MeterSource source, bool enabled,
            float volume, float pan, int pitchLow, int pitchHigh,
            WaveformType waveform = WaveformType.Sine)
        {
            if (index >= Slots.Count) return;
            var slot = Slots[index];
            slot.Source = source;
            slot.Enabled = enabled;
            slot.Volume = volume;
            slot.Pan = pan;
            slot.PitchLow = pitchLow;
            slot.PitchHigh = pitchHigh;
            slot.Waveform = waveform;
            slot.ToneProvider.Waveform = waveform;
        }

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
            EarconPlayer.RegisterContinuousTone(slot.ToneProvider, slot.Pan);
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
            if (!_enabled || _rig == null) return;

            // Throttle updates to ~10 Hz to avoid audio glitching
            long now = DateTime.UtcNow.Ticks;
            if (now - _lastUpdateTicks < ThrottleIntervalTicks) return;
            _lastUpdateTicks = now;

            bool transmitting = _rig.Transmit;

            // Update each slot that matches this meter type
            foreach (var slot in Slots)
            {
                if (!slot.Enabled) continue;
                var source = slot.Source;
                if (MeterTypeToSource(meter) != source) continue;

                // Auto-mute logic: S-meter only during RX, ALC/Mic/Power during TX
                bool shouldSound = ShouldSlotSound(source, transmitting);
                slot.ToneProvider.Active = shouldSound;

                if (shouldSound)
                {
                    float normalized = NormalizeMeterValue(source, value);
                    float freq = slot.PitchLow + (slot.PitchHigh - slot.PitchLow) * normalized;
                    slot.ToneProvider.Frequency = freq;
                    slot.ToneProvider.Volume = slot.Volume * MasterVolume;
                    slot.ToneProvider.Waveform = slot.Waveform;

                }
            }

            // Peak Watcher
            if (PeakWatcherEnabled && transmitting && meter == MeterType.ALC)
            {
                CheckPeakWatcher(value, now);
            }
        }

        private static void OnTransmitChanged(object sender, bool transmitting)
        {
            if (!_enabled) return;

            // When TX state changes, update which slots are active
            foreach (var slot in Slots)
            {
                if (!slot.Enabled) continue;
                bool shouldSound = ShouldSlotSound(slot.Source, transmitting);
                slot.ToneProvider.Active = shouldSound;
            }

            // Reset peak watcher state on TX→RX transition
            if (!transmitting)
            {
                _alcHighStartTicks = 0;
                _alcSustainedWarning = false;
            }
        }

        private static bool ShouldSlotSound(MeterSource source, bool transmitting)
        {
            return source switch
            {
                MeterSource.SMeter => !transmitting,
                MeterSource.ALC => transmitting,
                MeterSource.Mic => transmitting,
                MeterSource.Power => transmitting,
                MeterSource.SWR => transmitting,
                MeterSource.Compression => transmitting,
                MeterSource.Voltage => true,
                MeterSource.PATemp => true,
                _ => false
            };
        }

        private static MeterSource? MeterTypeToSource(MeterType type)
        {
            return type switch
            {
                MeterType.SMeter => MeterSource.SMeter,
                MeterType.ALC => MeterSource.ALC,
                MeterType.Mic => MeterSource.Mic,
                MeterType.Power => MeterSource.Power,
                MeterType.SWR => MeterSource.SWR,
                MeterType.Compression => MeterSource.Compression,
                MeterType.Voltage => MeterSource.Voltage,
                MeterType.PATemp => MeterSource.PATemp,
                _ => null
            };
        }

        /// <summary>
        /// Normalize a raw meter value to 0.0–1.0 range for pitch mapping.
        /// </summary>
        private static float NormalizeMeterValue(MeterSource source, float raw)
        {
            float normalized = source switch
            {
                // S-meter: raw is dBm, roughly -127 (S0) to -34 (S9+40)
                MeterSource.SMeter => Math.Clamp((raw + 127f) / 93f, 0f, 1f),

                // ALC: 0.0 (no ALC) to ~1.0+ (maxed)
                MeterSource.ALC => Math.Clamp(raw, 0f, 1f),

                // Mic: dBm, roughly -60 (silence) to 0 (loud).
                // Unity is around -20 dBm → maps to 0.5
                MeterSource.Mic => Math.Clamp((raw + 60f) / 60f, 0f, 1f),

                // Forward power: dBm, roughly 0 to 50 (100W = ~50 dBm)
                MeterSource.Power => Math.Clamp(raw / 50f, 0f, 1f),

                // SWR: 1.0 (perfect) to 10.0+ (bad). Map 1.0→0.0, 3.0→1.0
                MeterSource.SWR => Math.Clamp((raw - 1f) / 2f, 0f, 1f),

                // Compression: dBm range, map -30 to 0
                MeterSource.Compression => Math.Clamp((raw + 30f) / 30f, 0f, 1f),

                // Voltage: map 10V→0.0, 15V→1.0
                MeterSource.Voltage => Math.Clamp((raw - 10f) / 5f, 0f, 1f),

                // PA Temp: map 20°C→0.0, 80°C→1.0
                MeterSource.PATemp => Math.Clamp((raw - 20f) / 60f, 0f, 1f),

                _ => 0f
            };
            return normalized;
        }

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
                    ScreenReaderOutput.Speak("ALC high");
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
                        ScreenReaderOutput.Speak("ALC warning");
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
            ScreenReaderOutput.Speak(summary, true);
        }

        #endregion
    }

    /// <summary>Meter sources that can be assigned to tone slots.</summary>
    public enum MeterSource
    {
        SMeter, ALC, Mic, Power, SWR, Compression, Voltage, PATemp
    }

    /// <summary>
    /// A single meter tone slot with its own frequency mapping, volume, and panning.
    /// </summary>
    public class MeterSlot
    {
        public MeterSource Source { get; set; }
        public bool Enabled { get; set; }
        public float Volume { get; set; } = 0.5f;
        public float Pan { get; set; }
        public int PitchLow { get; set; } = 200;
        public int PitchHigh { get; set; } = 1200;
        public WaveformType Waveform { get; set; } = WaveformType.Sine;
        public ContinuousToneSampleProvider ToneProvider { get; } = new();
    }
}
