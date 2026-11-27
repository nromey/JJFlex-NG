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
        /// The ACTIVE slice's mode, never the transmit slice's (#1123, Sprint 49
        /// Track H2): what receive noise reduction follows when the operator
        /// moves to a slice in another mode. <see cref="Mode"/> reads the
        /// transmit mode while transmitting. Defaults to <see cref="Mode"/> for a
        /// radio that knows no difference (the tests' fake).
        /// </summary>
        string ReceiveMode => Mode;
        /// <summary>
        /// The TRANSMIT slice's mode (Sprint 49 Track H2, slice-mode round): what
        /// transmit noise reduction's mode policy and its summary follow, never
        /// the active slice's. Defaults to <see cref="Mode"/> for a radio that
        /// knows no difference.
        /// </summary>
        string TransmitMode => Mode;
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
        /// <summary>The radio's current level (for the one unreported level, NR Filter's, only what this session wrote, or zero).</summary>
        int ReadLevel(DenoiseEngineEntry engine);
        void WriteLevel(DenoiseEngineEntry engine, int level);
        /// <summary>Why a write of this capability would be refused, hardware first, then the licence; null when it is open.</summary>
        string? CapabilityRefusal(RadioCapability capability);
        /// <summary>Whether the capability is shut on this radio (for the marker on a menu row or list entry).</summary>
        bool CapabilityClosed(RadioCapability capability);
        /// <summary>The short marker for a closed capability: "not on this radio" or "needs subscription".</summary>
        bool ClosedByHardware(RadioCapability capability);

        // ── The radio's own nine-band graphic equaliser (Sprint 49 noise keys,
        //    #490's ruling of 2026-11-26: Ctrl+E in the noise layer). Through
        //    the rig's existing wrappers, the ones Home's Transmission and
        //    Receive equaliser buttons use (EqualizerLauncher); never FlexLib
        //    directly. Default bodies, so a radio that has none (the tests'
        //    fakes, a future radio) reads "not reported" and refuses a write. ──

        /// <summary>The radio's equaliser on a side, as a fresh copy; null when the radio has not reported it yet.</summary>
        FlexBase.EqSettings? ReadRadioEqualizer(DenoiseDirection side) => null;

        /// <summary>Write the radio's equaliser on a side, whole. False (and nothing written) when the radio has not reported it yet.</summary>
        bool WriteRadioEqualizer(DenoiseDirection side, FlexBase.EqSettings settings) => false;

        /// <summary>Ask the radio for its equaliser on a side, so a later read finds it. Safe to repeat.</summary>
        void RequestRadioEqualizer(DenoiseDirection side) { }
    }

    /// <summary>Per-radio held levels for the levels the radio never reports back (ruled 2026-11-07, question 16): today only NR Filter's, since the Track E follow-up reads Legacy NR, Spectral NR and Legacy Auto-Notch from the radio (#1012, S5, S12).</summary>
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
        public string ReceiveMode => _rig.RXMode ?? "";
        public string TransmitMode => TxAudioConditioning.TransmitModeOf(_rig);
        public object? SliceIdentity => _rig.ActiveSliceIdentity;
        public bool PcAudioOn => _rig.PCAudio;
        public bool ReadSwitch(DenoiseEngineEntry engine) => engine.ReadRadio?.Invoke(_rig) == FlexBase.OffOnValues.on;
        public void WriteSwitch(DenoiseEngineEntry engine, bool on) => engine.WriteRadio?.Invoke(_rig, on ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off);
        public int ReadLevel(DenoiseEngineEntry engine) => engine.Level?.Read(_rig) ?? 0;
        public void WriteLevel(DenoiseEngineEntry engine, int level) => engine.Level?.Write(_rig, level);
        public string? CapabilityRefusal(RadioCapability capability) => _rig.CapabilityWriteRefusal(capability);
        public bool CapabilityClosed(RadioCapability capability) => _rig.CapabilityIsClosedOnThisRadio(capability);
        public bool ClosedByHardware(RadioCapability capability) => _rig.CapabilityClosedReason(capability) == CapabilityBlock.Hardware;

        public FlexBase.EqSettings? ReadRadioEqualizer(DenoiseDirection side) =>
            side == DenoiseDirection.Transmit ? _rig.GetTxEq() : _rig.GetRxEq();

        public bool WriteRadioEqualizer(DenoiseDirection side, FlexBase.EqSettings settings) =>
            side == DenoiseDirection.Transmit
                ? _rig.ApplyTxEq(CopyEq<FlexBase.TxEqSettings>(settings))
                : _rig.ApplyRxEq(CopyEq<FlexBase.RxEqSettings>(settings));

        public void RequestRadioEqualizer(DenoiseDirection side)
        {
            if (side == DenoiseDirection.Transmit) _rig.RequestTxEqualizer(); else _rig.RequestRxEqualizer();
        }

        /// <summary>A side-typed copy, so a receive curve can never be handed to the transmit apply (FlexBase's own reason for two types).</summary>
        private static T CopyEq<T>(FlexBase.EqSettings from) where T : FlexBase.EqSettings, new()
        {
            var to = new T { Enabled = from.Enabled };
            for (int i = 0; i < FlexBase.EqBandHz.Length; i++) FlexBase.SetEqBand(to, i, FlexBase.GetEqBand(from, i));
            return to;
        }
    }
}
