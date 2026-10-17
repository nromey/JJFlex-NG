#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// One ladder for every gated capability, and the gate on the WRITE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the write and not the presentation.</b> #725 was a toggle that
    /// announced "on" while the radio ignored the command, because the key
    /// layer checked the model string and never the licence. The menu checked
    /// the licence correctly; the expander checked hardware only; the key layer
    /// checked hardware only. <b>Presentation gates multiply, and the newest
    /// road always forgets.</b> There is exactly one place every road passes
    /// through on its way to the radio — the setters — so that is where the gate
    /// belongs. Presentation gates are demoted to courtesy: they mark a label so
    /// the operator knows before pressing, and they stop being the thing that
    /// makes the behaviour correct.
    /// </para>
    /// <para>
    /// <b>A refused write is never silent.</b> It raises
    /// <see cref="CapabilityWriteRefused"/>. A setter that simply dropped the
    /// write would turn a wrong announcement into a mysterious no-op, which is
    /// worse — and these setters already drop silently when no slice is
    /// selected, so a new silent drop would be invisible among the existing
    /// ones. The event also means a road nobody has found yet is covered the day
    /// it is written, which a per-road gate can never promise.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        /// <summary>How much wording a caller wants.</summary>
        public enum CapabilityWording
        {
            /// <summary>A phrase for a key layer or a status field.</summary>
            Short,

            /// <summary>Menu-length prose that can name the alternative.</summary>
            Long,
        }

        /// <summary>A write was refused, with the reason, so the caller can say so.</summary>
        public sealed class CapabilityRefusedEventArgs : EventArgs
        {
            public CapabilityRefusedEventArgs(CapabilityVerdict verdict) => Verdict = verdict;

            public CapabilityVerdict Verdict { get; }
        }

        /// <summary>
        /// Raised when a write to a gated capability was refused rather than
        /// sent. <b>Subscribe once, centrally</b> — every road is covered by the
        /// one subscriber, and a second subscriber that also speaks would double
        /// the announcement.
        /// </summary>
        public event EventHandler<CapabilityRefusedEventArgs>? CapabilityWriteRefused;

        /// <summary>
        /// One row per capability. <b>Adding a gated feature is one row</b>, and
        /// <c>EveryCapabilityHasADescriptor</c> fails the build's test run if a
        /// row is forgotten, so the acceptance test is enforced rather than
        /// merely stated.
        /// </summary>
        private sealed class CapabilityDescriptor
        {
            public RadioCapability Capability { get; init; }

            /// <summary>Short operator-facing name, for announcements and tests.</summary>
            public string Name { get; init; } = string.Empty;

            /// <summary>Said when there is no radio at all.</summary>
            public string NotConnectedText { get; init; } = string.Empty;

            /// <summary>Said when no slice is selected, or null when a slice is not needed.</summary>
            public string? NoSliceText { get; init; }

            /// <summary>
            /// The hardware test, or null when the capability has no hardware
            /// requirement. Prefer a fact the radio reports about itself over a
            /// model-string match.
            /// </summary>
            public Func<FlexBase, bool>? Hardware { get; init; }

            /// <summary>Lexicon key for the short hardware refusal.</summary>
            public string? HardwareShortKey { get; init; }

            /// <summary>Menu-length hardware refusal. Names the alternative where one exists.</summary>
            public string HardwareLongText { get; init; } = string.Empty;

            /// <summary>The licence feature, or null when the capability is not licensed.</summary>
            public Func<FlexBase, Feature?>? Licence { get; init; }

            /// <summary>
            /// What to say when the radio has NOT reported the licence, or
            /// <b>null to stay quiet</b>.
            /// <para>
            /// This field IS the per-surface policy, as data. Diversity says
            /// "license status pending" because it fills a status field where
            /// pending is useful; the advanced-NR family says nothing because its
            /// answer decides whether a control exists, and guessing wrong there
            /// removes a working feature. Both were right and lived in different
            /// files; now the asymmetry is one line you can see.
            /// </para>
            /// </summary>
            public string? LicenceNotReportedText { get; init; }

            /// <summary>
            /// Used only when the radio supplies no <c>FeatureGatedMessage</c>.
            /// <b>The radio's own wording is preferred</b> — it is the vendor's
            /// sentence about the vendor's subscription.
            /// </summary>
            public string LicenceDisabledFallbackText { get; init; } = string.Empty;

            /// <summary>Menu-length licence refusal, when one differs from the short form.</summary>
            public string? LicenceDisabledLongText { get; init; }

            /// <summary>Operator-fixable preconditions. Returns the instruction, or null when satisfied.</summary>
            public Func<FlexBase, string?>? Resources { get; init; }

            /// <summary>Mode precondition. Returns the instruction, or null when satisfied.</summary>
            public Func<FlexBase, string?>? ModeRequirement { get; init; }
        }

        // The wording here is CARRIED FROM the two ladders this replaces, byte
        // for byte, on purpose: this change makes one ladder authoritative and
        // changes no sentence an operator hears. Refusal wording is Noel's call
        // (#720) and a rewording is a separate, deliberate step.
        private static readonly CapabilityDescriptor[] CapabilityTable = new[]
        {
            new CapabilityDescriptor
            {
                Capability = RadioCapability.NoiseReductionFilter,
                Name = "NR Filter",
                NotConnectedText = "No radio connected.",
                NoSliceText = "Select a slice",
                Hardware = r => r.NeuralNRHardwareSupported,
                HardwareShortKey = "audio.nr.filter_unsupported",
                HardwareLongText = AdvancedNrHardwareProse,
                Licence = r => r.theRadio?.FeatureLicense?.LicenseFeatNoiseReduction,
                LicenceNotReportedText = null,
                LicenceDisabledFallbackText = AdvancedNrLicenceProse,
                LicenceDisabledLongText = AdvancedNrLicenceProse,
                ModeRequirement = NoiseReductionModeRequirement,
            },
            new CapabilityDescriptor
            {
                Capability = RadioCapability.NoiseReductionSpectral,
                Name = "Spectral NR",
                NotConnectedText = "No radio connected.",
                NoSliceText = "Select a slice",
                Hardware = r => r.NeuralNRHardwareSupported,
                HardwareShortKey = "audio.nr.spectral_unsupported",
                HardwareLongText = AdvancedNrHardwareProse,
                Licence = r => r.theRadio?.FeatureLicense?.LicenseFeatNoiseReduction,
                LicenceNotReportedText = null,
                LicenceDisabledFallbackText = AdvancedNrLicenceProse,
                LicenceDisabledLongText = AdvancedNrLicenceProse,
                ModeRequirement = NoiseReductionModeRequirement,
            },
            new CapabilityDescriptor
            {
                Capability = RadioCapability.NoiseReductionNeural,
                Name = "Neural NR",
                NotConnectedText = "No radio connected.",
                NoSliceText = "Select a slice",
                Hardware = r => r.NeuralNRHardwareSupported,
                HardwareShortKey = "audio.nr.neural_unsupported",
                HardwareLongText = AdvancedNrHardwareProse,
                Licence = r => r.theRadio?.FeatureLicense?.LicenseFeatNoiseReduction,
                LicenceNotReportedText = null,
                LicenceDisabledFallbackText = AdvancedNrLicenceProse,
                LicenceDisabledLongText = AdvancedNrLicenceProse,
                ModeRequirement = NoiseReductionModeRequirement,
            },
            new CapabilityDescriptor
            {
                Capability = RadioCapability.DiversityReception,
                Name = "Diversity",
                // Carried verbatim from DiversityGateMessage, which has shipped
                // for months and whose strings appear in a status field.
                NotConnectedText = "Radio not ready",
                NoSliceText = "Select a slice",
                Hardware = r => r.theRadio?.DiversityIsAllowed == true,
                HardwareShortKey = null,
                HardwareLongText = "Model lacks diversity support",
                Licence = r => r.theRadio?.FeatureLicense?.LicenseFeatDivEsc,
                LicenceNotReportedText = "Diversity license status pending",
                LicenceDisabledFallbackText = "Purchase a diversity license to enable this feature",
                Resources = DiversityResourceRequirement,
            },
            new CapabilityDescriptor
            {
                Capability = RadioCapability.CwAutotune,
                Name = "CW Autotune",
                NotConnectedText = "No radio connected.",
                NoSliceText = "Select a slice",
                // The radio's own capability table, not a model string.
                Hardware = r => r.SupportsCwAutotune,
                HardwareShortKey = null,
                HardwareLongText = "This radio model does not support CW Autotune.",
                // CW Auto Tune is a SmartSDR+ feature. NOT the antenna tuner —
                // LicenseFeatAutotune gates the v4 slice feature that tunes to a
                // CW signal in the passband, while an ATU is unlicensed hardware
                // that predates the subscription scheme entirely. The names are
                // close enough that this brief got it wrong once (#726).
                Licence = r => r.theRadio?.FeatureLicense?.LicenseFeatAutotune,
                LicenceNotReportedText = null,
                LicenceDisabledFallbackText =
                    "Your radio reports that CW Autotune is not included in its current subscription.",
                ModeRequirement = CwAutotuneModeRequirement,
            },
        };

        // Carried verbatim from NativeMenuBar.AdvancedNrGateMessage so promoting
        // the ladder changes no sentence. Its hardware sentence already names
        // the PC-side alternative, which is why it is worth keeping intact.
        private const string AdvancedNrHardwareProse =
            "This radio model does not have the DSP hardware for the advanced "
            + "noise reduction family — RNN, spectral NR and the NR filter. Legacy NR "
            + "and the noise blankers above work on every model, and JJ Flex's own "
            + "PC-side noise reduction runs on this computer and works on every radio.";

        private const string AdvancedNrLicenceProse =
            "Your radio reports that the advanced noise reduction features are not "
            + "included in its current subscription. JJ Flex's own PC-side noise "
            + "reduction runs on this computer instead and needs no subscription.";

        private static string? NoiseReductionModeRequirement(FlexBase rig)
        {
            string mode = (rig.Mode ?? string.Empty).ToLowerInvariant();
            bool blocked = mode.StartsWith("cw", StringComparison.Ordinal) || mode.Contains("fm");
            return blocked ? "not available in CW or FM modes" : null;
        }

        private static string? CwAutotuneModeRequirement(FlexBase rig)
        {
            string mode = (rig.Mode ?? string.Empty).ToLowerInvariant();
            return mode.StartsWith("cw", StringComparison.Ordinal)
                ? null
                : "switch to CW mode to use autotune";
        }

        private static string? DiversityResourceRequirement(FlexBase rig)
        {
            var radio = rig.theRadio;
            if (radio == null) return null;

            if ((radio.RXAntList?.Length ?? 0) < 2) return "Need two RX antennas";

            // theRadio.MaxSlices CAME BACK 0 FROM A REAL REMOTE 6300 over
            // SmartLink, measured 2026-10-10, and a gate written as ">= 2"
            // then evaluates 0 >= 2 and switches the feature off silently -
            // as a capability simply not offered. Whether AvailableSlices
            // degrades the same way is NOT established; it never appeared in
            // that trace. So treat zero as "not reported" rather than as
            // "none", and cross-check the model-derived count, which is what
            // AnnounceSliceCensus already does. One reading off a radio over
            // SmartLink would settle it.
            int available = radio.AvailableSlices;
            if (available <= 0) return null;
            if (available < 2 && rig.TotalMaxSlices >= 2) return null;
            return available < 2 ? "Need two slices for diversity" : null;
        }

        private static CapabilityDescriptor DescriptorFor(RadioCapability capability)
        {
            foreach (var d in CapabilityTable)
            {
                if (d.Capability == capability) return d;
            }

            // Unreachable while the descriptor test passes. Throwing rather than
            // returning a permissive default on purpose: a missing row must not
            // read as "no gate needed", which is the failure this class exists
            // to end.
            throw new ArgumentOutOfRangeException(
                nameof(capability), capability, "No capability descriptor. Add a row to CapabilityTable.");
        }

        /// <summary>Every capability this build knows how to gate.</summary>
        public static IReadOnlyList<RadioCapability> GatedCapabilities
        {
            get
            {
                var list = new List<RadioCapability>(CapabilityTable.Length);
                foreach (var d in CapabilityTable) list.Add(d.Capability);
                return list;
            }
        }

        /// <summary>
        /// Walk the ladder for one capability. <b>The order is the design</b> —
        /// see <see cref="CapabilityBlock"/> for why hardware must precede any
        /// licence and resources must come last.
        /// </summary>
        public CapabilityVerdict EvaluateCapability(
            RadioCapability capability, CapabilityWording wording = CapabilityWording.Short)
        {
            var d = DescriptorFor(capability);

            if (theRadio == null)
                return new CapabilityVerdict(capability, CapabilityBlock.NotConnected, d.NotConnectedText);

            if (d.NoSliceText != null && !HasActiveSlice)
                return new CapabilityVerdict(capability, CapabilityBlock.NoSlice, d.NoSliceText);

            if (d.Hardware != null && !d.Hardware(this))
            {
                string message = wording == CapabilityWording.Long || d.HardwareShortKey == null
                    ? d.HardwareLongText
                    : Lexicon.Get(d.HardwareShortKey);
                return new CapabilityVerdict(capability, CapabilityBlock.Hardware, message);
            }

            if (d.Licence != null)
            {
                var feature = d.Licence(this);

                // Never reported is a THIRD STATE. A licence we have never been
                // told about is not evidence of anything - the radio may simply
                // not have sent its feature list yet - so the message may be
                // empty and the caller is expected to let the write through.
                if (feature == null)
                {
                    return new CapabilityVerdict(
                        capability, CapabilityBlock.LicenceNotReported, d.LicenceNotReportedText ?? string.Empty);
                }

                if (!feature.FeatureEnabled)
                {
                    // The radio's own sentence about its own subscription beats
                    // ours. Only fall back when it supplies none.
                    string message = feature.FeatureGatedMessage
                        ?? (wording == CapabilityWording.Long
                            ? (d.LicenceDisabledLongText ?? d.LicenceDisabledFallbackText)
                            : d.LicenceDisabledFallbackText);
                    return new CapabilityVerdict(capability, CapabilityBlock.LicenceDisabled, message);
                }
            }

            if (d.Resources != null)
            {
                string? why = d.Resources(this);
                if (why != null) return new CapabilityVerdict(capability, CapabilityBlock.Resource, why);
            }

            if (d.ModeRequirement != null)
            {
                string? why = d.ModeRequirement(this);
                if (why != null) return new CapabilityVerdict(capability, CapabilityBlock.Mode, why);
            }

            return new CapabilityVerdict(capability, CapabilityBlock.None, string.Empty);
        }

        /// <summary>
        /// Why this capability is closed, or <b>null when it is available</b> —
        /// the <c>string?</c> shape the key layer's own gate helpers already use.
        /// </summary>
        /// <remarks>
        /// A capability whose licence was never reported returns null here: not
        /// knowing is not a reason to refuse.
        /// </remarks>
        public string? CapabilityRefusal(
            RadioCapability capability, CapabilityWording wording = CapabilityWording.Short)
        {
            var verdict = EvaluateCapability(capability, wording);
            if (verdict.Allowed) return null;
            if (verdict.Block == CapabilityBlock.LicenceNotReported) return null;
            return string.IsNullOrEmpty(verdict.Message) ? null : verdict.Message;
        }

        /// <summary>
        /// The gate on the write. Returns true when the caller must NOT send,
        /// having already raised <see cref="CapabilityWriteRefused"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not-reported lets the write through.</b> Anything that refuses or
        /// removes treats an unreported licence as "allow and stay quiet", so a
        /// radio that has not yet sent its feature list is never denied a
        /// feature it owns. The radio refuses for itself if we are wrong, which
        /// is the failure we can recover from; silently removing a paid feature
        /// is not.
        /// </para>
        /// <para>
        /// <b>And an expired subscription does not revoke anything.</b>
        /// FlexRadio: "When your one-year feature license expires, you retain
        /// all features received during that period--you never lose them." So
        /// entitlement is whatever <c>FeatureLicense</c> says and must never be
        /// computed from whether a subscription looks current.
        /// </para>
        /// </remarks>
        internal bool RefuseCapabilityWrite(RadioCapability capability)
        {
            var verdict = EvaluateCapability(capability, CapabilityWording.Short);

            if (verdict.Allowed) return false;
            if (verdict.Block == CapabilityBlock.LicenceNotReported) return false;

            // NOT CONNECTED AND NO SLICE DROP THE WRITE SILENTLY, AND THAT IS
            // DELIBERATE RATHER THAN AN OVERSIGHT.
            //
            // These setters ALREADY dropped the write silently when no slice was
            // selected, so the behaviour is unchanged - but the reason is what
            // matters. "The radio is not here yet" is not a statement about the
            // capability, and a panel that writes a switch while building itself
            // or tearing itself down would otherwise make the application
            // announce "No radio connected" at the operator for a write they
            // never asked for. Spurious speech is its own defect class here, and
            // a gate that creates one while closing another has not helped.
            //
            // The event fires only for the classes that mean "this capability is
            // closed to you": hardware, a positively-disabled licence, a
            // resource, or the wrong mode. Those are the ones where the operator
            // pressed something and deserves to hear why nothing happened.
            if (verdict.Block == CapabilityBlock.NotConnected || verdict.Block == CapabilityBlock.NoSlice)
            {
                Tracing.TraceLine(
                    "CapabilityWrite dropped (transient): " + capability + " block=" + verdict.Block,
                    TraceLevel.Verbose);
                return true;
            }

            Tracing.TraceLine(
                "CapabilityWrite refused: " + capability + " block=" + verdict.Block
                + " msg=" + verdict.Message, TraceLevel.Info);

            try
            {
                CapabilityWriteRefused?.Invoke(this, new CapabilityRefusedEventArgs(verdict));
            }
            catch (Exception ex)
            {
                // A subscriber that throws must not turn a refusal into a crash
                // on the radio's thread.
                Tracing.TraceLine("CapabilityWriteRefused handler threw: " + ex.Message, TraceLevel.Error);
            }

            return true;
        }
    }
}
