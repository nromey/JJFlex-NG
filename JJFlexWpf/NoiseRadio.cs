using System;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// The radio as the noise controller sees it: a switch and a level per
    /// catalog row, the capability ladder, the mode and the serial. An
    /// interface so the controller's tests run against a fake radio on the
    /// live desk, with no FlexBase and no window.
    /// </summary>
    public interface INoiseRadio
    {
        /// <summary>The radio's serial, the key per-radio intents are kept under; empty when unknown.</summary>
        string Serial { get; }
        string Mode { get; }
        /// <summary>
        /// The slice every read and write here acts on right now: an opaque
        /// identity, compared by reference, never a number (repair round 2,
        /// B1). Null when no slice is active.
        /// </summary>
        object? SliceIdentity { get; }
        /// <summary>Whether the radio's audio is playing through this computer, so the computer's engines have audio to work on.</summary>
        bool PcAudioOn { get; }
        bool ReadSwitch(DenoiseEngineEntry engine);
        void WriteSwitch(DenoiseEngineEntry engine, bool on);
        /// <summary>The radio's current level (for an unreported level, only what this session wrote, or zero).</summary>
        int ReadLevel(DenoiseEngineEntry engine);
        void WriteLevel(DenoiseEngineEntry engine, int level);
        /// <summary>Why a write of this capability would be refused, hardware first, then the licence; null when it is open.</summary>
        string? CapabilityRefusal(RadioCapability capability);
        /// <summary>Whether the capability is shut on this radio (for the marker on a menu row or list entry).</summary>
        bool CapabilityClosed(RadioCapability capability);
        /// <summary>The short marker for a closed capability: "not on this radio" or "needs subscription".</summary>
        bool ClosedByHardware(RadioCapability capability);
    }

    /// <summary>Per-radio held levels for the four levels the radio never reports back (ruled 2026-11-07, question 16).</summary>
    public interface INoiseIntentStore
    {
        int? Get(string serial, string engineId);
        void Set(string serial, string engineId, int level);
    }

    /// <summary>The app's store: <see cref="RadioConfig"/>, by serial.</summary>
    public sealed class RadioConfigNoiseIntents : INoiseIntentStore
    {
        public int? Get(string serial, string engineId) => RadioConfig.NoiseLevelIntentOf(serial, engineId);
        public void Set(string serial, string engineId, int level) => RadioConfig.RecordNoiseLevelIntent(serial, engineId, level);
    }

    /// <summary>The live radio, through each catalog row's own delegates.</summary>
    public sealed class FlexNoiseRadio : INoiseRadio
    {
        private readonly FlexBase _rig;
        public FlexNoiseRadio(FlexBase rig) => _rig = rig ?? throw new ArgumentNullException(nameof(rig));
        public string Serial => _rig.SelectedRadioSerial ?? "";
        public string Mode => _rig.Mode ?? "";
        public object? SliceIdentity => _rig.ActiveSliceIdentity;
        public bool PcAudioOn => _rig.PCAudio;
        public bool ReadSwitch(DenoiseEngineEntry engine) => engine.ReadRadio?.Invoke(_rig) == FlexBase.OffOnValues.on;
        public void WriteSwitch(DenoiseEngineEntry engine, bool on) => engine.WriteRadio?.Invoke(_rig, on ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off);
        public int ReadLevel(DenoiseEngineEntry engine) => engine.Level?.Read(_rig) ?? 0;
        public void WriteLevel(DenoiseEngineEntry engine, int level) => engine.Level?.Write(_rig, level);
        public string? CapabilityRefusal(RadioCapability capability) => _rig.CapabilityWriteRefusal(capability);
        public bool CapabilityClosed(RadioCapability capability) => _rig.CapabilityIsClosedOnThisRadio(capability);
        public bool ClosedByHardware(RadioCapability capability) => _rig.CapabilityClosedReason(capability) == CapabilityBlock.Hardware;
    }
}
