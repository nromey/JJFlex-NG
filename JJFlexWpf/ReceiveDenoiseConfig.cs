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
                config.RNNoiseAutoDisableNonVoice = pipeline.VoiceModesOnly;
                config.SpectralSubEnabled = pipeline.SpectralEnabled;
                config.SpectralSubStrength = pipeline.SpectralStrength;
                config.SpectralSubFloor = pipeline.SpectralFloor;
                config.ReceiveVoiceModesOnly = pipeline.VoiceModesOnly;
                return;
            }
            if (config.ReceiveDenoiseVersion >= 1)
            {
                Restore(pipeline, pipeline.NeuralSlot, config.ReceiveNeural);
                Restore(pipeline, pipeline.SpectralSlot, config.ReceiveSpectral);
                pipeline.VoiceModesOnly = config.ReceiveVoiceModesOnly;
            }
            else
            {
                pipeline.RnnStrength = config.RNNoiseStrength;
                pipeline.SpectralStrength = config.SpectralSubStrength;
                pipeline.SpectralFloor = config.SpectralSubFloor;
                pipeline.RnnEnabled = config.RNNoiseEnabled;
                pipeline.SpectralEnabled = config.SpectralSubEnabled;
                pipeline.VoiceModesOnly = config.RNNoiseAutoDisableNonVoice;
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
