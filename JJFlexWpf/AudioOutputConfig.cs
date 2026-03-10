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

        /// <summary>Master earcon volume 0–100.</summary>
        public int MasterEarconVolume { get; set; } = 80;

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

        /// <summary>Whether tuning speech debounce is enabled. When false, every tuning step speaks immediately.</summary>
        public bool TuneDebounceEnabled { get; set; } = true;

        /// <summary>Tuning speech debounce delay in milliseconds (50-1000, default 300).</summary>
        public int TuneDebounceMs { get; set; } = 300;

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
            EarconPlayer.MasterVolume = MasterEarconVolume / 100f;
            if (EarconDeviceNumber != -1)
                EarconPlayer.SetOutputDevice(EarconDeviceNumber);

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
            MeterTonesEnabled = MeterToneEngine.Enabled;
            MeterPreset = MeterToneEngine.CurrentPreset;
            MeterMasterVolume = MeterToneEngine.MasterVolume;
            PeakWatcherEnabled = MeterToneEngine.PeakWatcherEnabled;
            MeterSpeechEnabled = MeterToneEngine.SpeechEnabled;
            MeterSpeechTimerActive = MeterToneEngine.SpeechTimerActive;
            MeterSpeechIntervalSeconds = MeterToneEngine.SpeechIntervalSeconds;
            AutoEnableOnTune = MeterToneEngine.AutoEnableOnTune;
            MasterEarconVolume = (int)(EarconPlayer.MasterVolume * 100);
        }
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
