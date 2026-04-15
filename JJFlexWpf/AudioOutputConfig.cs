using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Xml.Serialization;

namespace JJFlexWpf
{
    /// <summary>
    /// Persisted audio output settings: earcon device, master volume, and meter tone configuration.
    /// XML serialized per-operator following the same pattern as FilterPresets.
    /// </summary>
    [XmlRoot("AudioOutputConfig")]
    public class AudioOutputConfig
    {
        /// <summary>NAudio device number for earcon output. -1 = Windows default.</summary>
        public int EarconDeviceNumber { get; set; } = -1;

        /// <summary>Master earcon volume 0–100. Kept for backward compatibility with old configs.</summary>
        public int MasterEarconVolume { get; set; } = 80;

        /// <summary>Alert channel volume 0.0–1.0. Replaces int-based MasterEarconVolume.
        /// Defaults to -1 which means "derive from MasterEarconVolume" for backward compat.</summary>
        private float _alertVolume = -1f;
        public float AlertVolume
        {
            get => _alertVolume >= 0 ? _alertVolume : MasterEarconVolume / 100f;
            set => _alertVolume = value;
        }

        /// <summary>Master volume multiplier across all channels 0.0–1.0.</summary>
        public float MasterVolume { get; set; } = 1.0f;

        /// <summary>NAudio device number for meter tone output. -1 = same as alerts.</summary>
        public int MeterDeviceNumber { get; set; } = -1;

        /// <summary>Whether meter tones are enabled.</summary>
        public bool MeterTonesEnabled { get; set; }

        /// <summary>Active meter preset name.</summary>
        public string MeterPreset { get; set; } = "RX Monitor";

        /// <summary>Master meter tone volume 0.0–1.0.</summary>
        public float MeterMasterVolume { get; set; } = 0.5f;

        /// <summary>Whether Peak Watcher ALC alerts are enabled.</summary>
        public bool PeakWatcherEnabled { get; set; } = true;

        /// <summary>Whether meter speech readout is enabled.</summary>
        public bool MeterSpeechEnabled { get; set; } = true;

        /// <summary>Whether the periodic speech timer is active (speaks meter values at interval).</summary>
        public bool MeterSpeechTimerActive { get; set; }

        /// <summary>Speech interval in seconds (1-10).</summary>
        public int MeterSpeechIntervalSeconds { get; set; } = 3;

        /// <summary>Auto-enable meter tones when tune carrier is activated.</summary>
        public bool AutoEnableOnTune { get; set; }

        /// <summary>Speech verbosity level: 0=Off(Critical only), 1=Terse, 2=Chatty (default).</summary>
        public int SpeechVerbosity { get; set; } = 2; // VerbosityLevel.Chatty

        /// <summary>Whether alert sounds (earcons, beeps, tones) are enabled. Meter tones are separate.</summary>
        public bool EarconsEnabled { get; set; } = true;

        /// <summary>Frequency entry typing sound mode.</summary>
        public TypingSoundMode TypingSound { get; set; } = TypingSoundMode.Beep;

        /// <summary>Calibration tuning hash — stores verified reference data.</summary>
        public string TuningHash { get; set; } = "";

        /// <summary>Whether tuning speech debounce is enabled. When false, every tuning step speaks immediately.</summary>
        public bool TuneDebounceEnabled { get; set; } = true;

        /// <summary>Tuning speech debounce delay in milliseconds (50-1000, default 300).</summary>
        public int TuneDebounceMs { get; set; } = 300;

        /// <summary>Whether JJ Neural NR (RNNoise) is enabled.</summary>
        public bool RNNoiseEnabled { get; set; }

        /// <summary>RNNoise wet/dry mix strength 0.0-1.0.</summary>
        public float RNNoiseStrength { get; set; } = 0.8f;

        /// <summary>Auto-disable RNNoise in CW/digital modes.</summary>
        public bool RNNoiseAutoDisableNonVoice { get; set; } = true;

        /// <summary>Whether JJ Trained NR (spectral subtraction) is enabled.</summary>
        public bool SpectralSubEnabled { get; set; }

        /// <summary>Spectral subtraction strength 0.0-1.0.</summary>
        public float SpectralSubStrength { get; set; } = 0.7f;

        /// <summary>Noise sampling duration in seconds (1-5).</summary>
        public int SpectralSubSampleDuration { get; set; } = 2;

        /// <summary>Whether CW Morse code notifications are enabled (AS/BT/SK prosigns).</summary>
        public bool CwNotificationsEnabled { get; set; }

        /// <summary>CW sidetone frequency in Hz (400-1200, default 700).</summary>
        public int CwSidetoneHz { get; set; } = 700;

        /// <summary>CW notification speed in WPM (10-30, default 20).</summary>
        public int CwSpeedWpm { get; set; } = 20;

        /// <summary>Announce mode changes in CW when speech verbosity is Off.</summary>
        public bool CwModeAnnounce { get; set; }

        /// <summary>Whether braille status line is enabled.</summary>
        public bool BrailleEnabled { get; set; }

        /// <summary>Braille display cell count (20, 32, 40, 80).</summary>
        public int BrailleCellCount { get; set; } = 40;

        /// <summary>Braille display enabled fields (flags enum as int for XML serialization).</summary>
        public int BrailleFields { get; set; } = (int)JJFlexWpf.BrailleFields.All;

        /// <summary>
        /// Whether the panadapter / waterfall braille display is visible and in the tab order.
        /// When false, PanadapterPanel is collapsed (removed from layout and focus) and the
        /// per-tile braille callback skips its Tolk.Braille push so braille displays aren't
        /// refreshed with data the user isn't viewing. Default true preserves existing behavior.
        /// </summary>
        public bool ShowPanadapter { get; set; } = true;

        /// <summary>Per-slot meter tone configurations.</summary>
        public List<MeterSlotConfig> MeterSlots { get; set; } = new();

        private const string FileName = "audioConfig.xml";

        public static AudioOutputConfig Load(string configDir)
        {
            string path = Path.Combine(configDir, FileName);
            if (!File.Exists(path))
                return new AudioOutputConfig();

            try
            {
                var serializer = new XmlSerializer(typeof(AudioOutputConfig));
                using var stream = File.OpenRead(path);
                return (AudioOutputConfig?)serializer.Deserialize(stream) ?? new AudioOutputConfig();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"AudioOutputConfig.Load failed: {ex.Message}");
                return new AudioOutputConfig();
            }
        }

        public void Save(string configDir)
        {
            try
            {
                Directory.CreateDirectory(configDir);
                string path = Path.Combine(configDir, FileName);
                var serializer = new XmlSerializer(typeof(AudioOutputConfig));
                using var stream = File.Create(path);
                serializer.Serialize(stream, this);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"AudioOutputConfig.Save failed: {ex.Message}");
            }
        }

        /// <summary>Apply this config to the MeterToneEngine and EarconPlayer.</summary>
        public void Apply()
        {
            EarconPlayer.EarconsEnabled = EarconsEnabled;
            EarconPlayer.MasterVolume = MasterVolume;
            EarconPlayer.AlertVolume = AlertVolume;
            EarconPlayer.SetAlertDevice(EarconDeviceNumber);
            EarconPlayer.SetMeterDevice(MeterDeviceNumber);

            // Verbosity
            Radios.ScreenReaderOutput.CurrentVerbosity =
                (Radios.VerbosityLevel)Math.Clamp(SpeechVerbosity, 0, 2);

            MeterToneEngine.Enabled = MeterTonesEnabled;
            MeterToneEngine.MasterVolume = MeterMasterVolume;
            MeterToneEngine.PeakWatcherEnabled = PeakWatcherEnabled;
            MeterToneEngine.SpeechEnabled = MeterSpeechEnabled;
            MeterToneEngine.SpeechIntervalSeconds = Math.Clamp(MeterSpeechIntervalSeconds, 1, 10);
            MeterToneEngine.AutoEnableOnTune = AutoEnableOnTune;
            MeterToneEngine.SpeechTimerActive = MeterSpeechTimerActive;
            MeterToneEngine.ApplyPreset(MeterPreset ?? "RX Monitor");
        }

        /// <summary>Capture current state from the engine into this config.</summary>
        public void CaptureFromEngine()
        {
            SpeechVerbosity = (int)Radios.ScreenReaderOutput.CurrentVerbosity;
            MeterTonesEnabled = MeterToneEngine.Enabled;
            MeterPreset = MeterToneEngine.CurrentPreset;
            MeterMasterVolume = MeterToneEngine.MasterVolume;
            PeakWatcherEnabled = MeterToneEngine.PeakWatcherEnabled;
            MeterSpeechEnabled = MeterToneEngine.SpeechEnabled;
            MeterSpeechTimerActive = MeterToneEngine.SpeechTimerActive;
            MeterSpeechIntervalSeconds = MeterToneEngine.SpeechIntervalSeconds;
            AutoEnableOnTune = MeterToneEngine.AutoEnableOnTune;
            EarconsEnabled = EarconPlayer.EarconsEnabled;
            MasterVolume = EarconPlayer.MasterVolume;
            AlertVolume = EarconPlayer.AlertVolume;
            MasterEarconVolume = (int)(EarconPlayer.AlertVolume * 100);
            EarconDeviceNumber = EarconPlayer.GetAlertDeviceNumber();
            MeterDeviceNumber = EarconPlayer.GetMeterDeviceNumber();
        }
    }

    /// <summary>Frequency entry typing sound mode.</summary>
    public enum TypingSoundMode
    {
        /// <summary>Random musical notes from C4-C8 (always available). Display: "Musical notes".</summary>
        Beep,
        /// <summary>No sound on keystrokes.</summary>
        Off,
        /// <summary>Mechanical keyboard sounds (requires calibration unlock).</summary>
        Mechanical,
        /// <summary>DTMF touch-tone sounds (requires calibration unlock).</summary>
        TouchTone,
        /// <summary>Fixed pitch beep every keystroke (always available).</summary>
        SingleTone,
        /// <summary>Random frequency beep, not snapped to musical notes (always available).</summary>
        RandomTones
    }

    /// <summary>Per-slot configuration for XML serialization.</summary>
    public class MeterSlotConfig
    {
        public MeterSource Source { get; set; }
        public bool Enabled { get; set; }
        public float Volume { get; set; } = 0.5f;
        public float Pan { get; set; }
        public int PitchLow { get; set; } = 200;
        public int PitchHigh { get; set; } = 1200;
        public WaveformType Waveform { get; set; } = WaveformType.Sine;
    }
}
