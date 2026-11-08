using System.Collections.Generic;
using System.Linq;
using Radios;

namespace JJFlexWpf
{
    public sealed class ReceiveDenoiseSlotConfig
    {
        public bool Enabled { get; set; }
        // Empty until this slot has actually been turned on. Track E uses
        // this distinction for the first-use default.
        public string EngineId { get; set; } = "";
        public List<DenoiseEngineSettings> Engines { get; set; } = new();
    }

    public static class ReceiveDenoiseConfig
    {
        /// <summary>The single receive mapping, used by both UI call sites and offline tests.</summary>
        public static void Map(AudioOutputConfig config, RxAudioPipeline pipeline, bool save)
        {
            if (save)
            {
                config.ReceiveDenoiseVersion = 1;
                config.ReceiveNeural = Export(pipeline.NeuralSlot);
                config.ReceiveSpectral = Export(pipeline.SpectralSlot);
                // Dual-write the legacy fields for builds that know only these.
                config.RNNoiseEnabled = pipeline.RnnEnabled;
                config.RNNoiseStrength = pipeline.RnnStrength;
                config.SpectralSubEnabled = pipeline.SpectralEnabled;
                config.SpectralSubStrength = pipeline.SpectralStrength;
                config.SpectralSubFloor = pipeline.SpectralFloor;
                // ReceiveVoiceModesOnly and RNNoiseAutoDisableNonVoice are no
                // longer written: the switch is retired (Sprint 49 Track E), and
                // leaving the loaded values as they are means an older build
                // reading this file behaves exactly as it did before.
                return;
            }
            // The cross-fade switch (#873; Settings, Advanced) is app-wide.
            PcDenoiseSlot.CrossFadeEnabled = config.NoiseCrossFade;
            if (config.ReceiveDenoiseVersion >= 1)
            {
                Restore(pipeline, pipeline.NeuralSlot, config.ReceiveNeural);
                Restore(pipeline, pipeline.SpectralSlot, config.ReceiveSpectral);
                // ReceiveVoiceModesOnly is read nowhere: per-mode memory has
                // replaced the gate it controlled (ruled 2026-11-07, question 4).
            }
            else
            {
                pipeline.RnnStrength = config.RNNoiseStrength;
                pipeline.SpectralStrength = config.SpectralSubStrength;
                pipeline.SpectralFloor = config.SpectralSubFloor;
                pipeline.RnnEnabled = config.RNNoiseEnabled;
                pipeline.SpectralEnabled = config.SpectralSubEnabled;
            }
        }
        private static ReceiveDenoiseSlotConfig Export(PcDenoiseSlot slot) => new() {
            Enabled = slot.Enabled, EngineId = slot.HasBeenEnabled ? slot.ChosenEngineId : "",
            Engines = slot.ExportSettings().ToList()
        };
        private static void Restore(RxAudioPipeline pipeline, PcDenoiseSlot slot, ReceiveDenoiseSlotConfig? config)
        {
            if (config == null) return;
            slot.RestoreSettings(config.Engines);
            if (!string.IsNullOrEmpty(config.EngineId))
            {
                // Remembered intent remains even if its factory cannot start.
                if (slot.Select(config.EngineId, out _)) slot.RememberUse();
            }
            slot.Enabled = config.Enabled;
        }
    }
}
