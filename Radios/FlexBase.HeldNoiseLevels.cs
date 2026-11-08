using System;
using System.Diagnostics;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The four noise levels the radio never reports back (ruled 2026-11-07,
    /// question 16): Legacy NR, Spectral NR, NR Filter and Legacy Auto-Notch.
    /// FlexLib's status parser has no case for any of them, so the app holds
    /// each as an intent per radio and writes it.
    /// </summary>
    public enum HeldNoiseLevel
    {
        LegacyNoiseReduction,
        SpectralNoiseReduction,
        NoiseReductionFilter,
        LegacyAutoNotch,
    }

    /// <summary>
    /// FlexBase, the write path for the held noise levels (Sprint 49 Track E
    /// repair round 1, B7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these four cannot use their ordinary setters.</b> FlexLib's
    /// setter for each (<c>Slice.NRL_Level</c>, <c>NRSLevel</c>,
    /// <c>NRFLevel</c>, <c>ANFL_Level</c>) begins
    /// <c>if (value == _field) return;</c>, comparing against its own cached
    /// field. For a reported level that cache follows the radio; for these
    /// four nothing ever updates it, so it holds only what FlexLib itself last
    /// wrote, starting at 0 on a fresh slice. A held intent equal to that cache
    /// (a saved 0 on a new connection, or any value FlexLib wrote earlier to a
    /// slice that has since been changed by another program) was therefore
    /// dropped without a command, while the app believed it written. The level
    /// exists as an intent precisely because the cache cannot be trusted.
    /// </para>
    /// <para>
    /// <b>What this does instead, without editing vendored FlexLib.</b> When
    /// the cache differs, the ordinary setter runs, which sends the command
    /// and keeps the cache honest. When the cache already equals the value,
    /// the command FlexLib would have sent is sent directly through the
    /// radio's own command path, <see cref="Radio.SendReplyCommand"/>, the way
    /// the Loopback slice writes in this class do, and its reply is traced.
    /// Either way the trace says what went out.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>The slice command key FlexLib's own setter sends for each held level, read from <c>Slice.cs</c>.</summary>
        internal static string HeldNoiseLevelWireKey(HeldNoiseLevel which) => which switch
        {
            HeldNoiseLevel.LegacyNoiseReduction => "lms_nr_level",
            HeldNoiseLevel.SpectralNoiseReduction => "speex_nr_level",
            HeldNoiseLevel.NoiseReductionFilter => "nrf_level",
            HeldNoiseLevel.LegacyAutoNotch => "lms_anf_level",
            _ => throw new ArgumentOutOfRangeException(nameof(which)),
        };

        /// <summary>
        /// The command to send directly for a held level, or null when
        /// FlexLib's setter will send it itself. Null exactly when the clamped
        /// value differs from FlexLib's cache, which is the one case its setter
        /// does not drop. The clamp is FlexLib's own, 0 to 100.
        /// </summary>
        internal static string? HeldNoiseLevelDirectCommand(int sliceIndex, HeldNoiseLevel which, int cached, int wanted)
        {
            int value = Math.Clamp(wanted, 0, 100);
            return value == cached ? $"slice set {sliceIndex} {HeldNoiseLevelWireKey(which)}={value}" : null;
        }

        private static int CachedHeldNoiseLevel(Slice slice, HeldNoiseLevel which) => which switch
        {
            HeldNoiseLevel.LegacyNoiseReduction => slice.NRL_Level,
            HeldNoiseLevel.SpectralNoiseReduction => slice.NRSLevel,
            HeldNoiseLevel.NoiseReductionFilter => slice.NRFLevel,
            _ => slice.ANFL_Level,
        };

        private static void SetHeldNoiseLevelThroughSlice(Slice slice, HeldNoiseLevel which, int value)
        {
            switch (which)
            {
                case HeldNoiseLevel.LegacyNoiseReduction: slice.NRL_Level = value; break;
                case HeldNoiseLevel.SpectralNoiseReduction: slice.NRSLevel = value; break;
                case HeldNoiseLevel.NoiseReductionFilter: slice.NRFLevel = value; break;
                default: slice.ANFL_Level = value; break;
            }
        }

        /// <summary>
        /// Write a held noise level to the active slice so that it reaches the
        /// radio whatever FlexLib's cache says. Gated as the ordinary setter is
        /// (Spectral NR and NR Filter on the model and licence; Legacy NR and
        /// Legacy Auto-Notch were measured ungated, #746). Queued like every
        /// other slice write; the trace names the command that went out.
        /// </summary>
        public void WriteHeldNoiseLevel(HeldNoiseLevel which, int value)
        {
            if (which == HeldNoiseLevel.SpectralNoiseReduction && RefuseCapabilityWrite(RadioCapability.NoiseReductionSpectral)) return;
            if (which == HeldNoiseLevel.NoiseReductionFilter && RefuseCapabilityWrite(RadioCapability.NoiseReductionFilter)) return;
            if (!HasActiveSlice) return;
            NoteOperatorChangedRadioSetting(which switch
            {
                HeldNoiseLevel.LegacyNoiseReduction => "NoiseReductionLegacyLevel",
                HeldNoiseLevel.SpectralNoiseReduction => "SpectralNoiseReductionLevel",
                HeldNoiseLevel.NoiseReductionFilter => "NoiseReductionFilterLevel",
                _ => "AutoNotchLegacyLevel",
            });
            q.Enqueue((FunctionDel)(() =>
            {
                var radio = theRadio;
                var slice = radio?.ActiveSlice;
                if (radio == null || slice == null) return;
                int cached = CachedHeldNoiseLevel(slice, which);
                string? direct = HeldNoiseLevelDirectCommand(slice.Index, which, cached, value);
                if (direct == null)
                {
                    SetHeldNoiseLevelThroughSlice(slice, which, value);
                    Tracing.TraceLine($"HeldNoiseLevel {which}: sent through FlexLib's setter, slice set {slice.Index} {HeldNoiseLevelWireKey(which)}={Math.Clamp(value, 0, 100)} (its cache held {cached})", TraceLevel.Info);
                    return;
                }
                int sent = radio.SendReplyCommand((sequence, code, response) =>
                    Tracing.TraceLine($"HeldNoiseLevel {which}: reply {sequence} {code:X8} {response}", TraceLevel.Info), direct);
                Tracing.TraceLine($"HeldNoiseLevel {which}: sent {sent} {direct} directly; FlexLib's cache already held {cached}, so its setter would have sent nothing", TraceLevel.Info);
            }), "HeldNoiseLevel");
        }
    }
}
