#nullable enable
using System;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// A capability the radio can refuse, and which we therefore have to ask
    /// about in one place instead of guessing at each control.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Membership is deliberately small.</b> FlexLib exposes thirteen licence
    /// features; this enum reads three of them (<c>noise_reduction</c>,
    /// <c>div_esc</c> and <c>auto_tune</c>). The test for joining is not "is it
    /// licensed" but <b>does the radio refuse in a way the operator can
    /// perceive</b>. SmartLink and MultiFlex fail at connect, loudly and
    /// self-explainingly, so a client-side gate would add nothing. The radio's
    /// own Digital Voice Keyer has no road in this application at all, by
    /// decision rather than omission. The rest are read nowhere in our code.
    /// The verdict for each of the thirteen is recorded in #726, and
    /// <c>CapabilityGateTests</c> holds every item FlexLib parses to either a
    /// row here or a named, reasoned absence, so a fourteenth item cannot
    /// arrive unconsidered (#746: NRL, ANFL and ANFT were licensed switches
    /// that #726's own table never reached).
    /// </para>
    /// <para>
    /// <b>Adding a gated feature must be ONE entry here.</b> That is the
    /// acceptance test for this whole design: if you find yourself writing a
    /// gate in a UI file, this table is missing a row, and the next surface
    /// somebody builds will forget the gate exactly as the last one did (#725).
    /// </para>
    /// </remarks>
    public enum RadioCapability
    {
        /// <summary>Noise Reduction with Filter (NRF). 8000-series/Aurora DSP.</summary>
        NoiseReductionFilter,

        /// <summary>Spectral noise reduction (NRS). 8000-series/Aurora DSP.</summary>
        NoiseReductionSpectral,

        /// <summary>Neural / recurrent-network noise reduction (RNN). 8000-series/Aurora DSP.</summary>
        NoiseReductionNeural,

        /// <summary>Diversity reception. Two SCUs, and NO licence -- it is a
        /// v3-era hardware capability. Enhanced Signal Clarity is a SEPARATE
        /// capability that runs inside it and does carry a licence; see
        /// <see cref="Esc"/>. This comment read "including Enhanced Signal
        /// Clarity" until 2026-10-18, which was the conflation behind #745
        /// stated in one sentence.</summary>
        DiversityReception,

        /// <summary>Enhanced Signal Clarity (ESC). Two SCUs AND the div_esc
        /// licence -- a v4 feature, so SmartSDR+. Runs inside a diversity
        /// pair.</summary>
        Esc,

        /// <summary>CW Auto Tune — tunes the active slice to a CW signal in the passband.</summary>
        CwAutotune,

        /// <summary>Legacy LMS noise reduction (NRL). Runs on every model, so
        /// no hardware rung, but it is a SmartSDR+ algorithm on every series
        /// and reads the <c>noise_reduction</c> licence item (#746).</summary>
        NoiseReductionLegacy,

        /// <summary>FFT auto-notch (ANFT). Every model; licensed like
        /// <see cref="NoiseReductionLegacy"/> (#746). The noise layer's A key
        /// writes this one.</summary>
        AutoNotchFft,

        /// <summary>Legacy LMS auto-notch (ANFL). Every model; licensed like
        /// <see cref="NoiseReductionLegacy"/> (#746).</summary>
        AutoNotchLegacy,
    }

    /// <summary>
    /// WHY a capability is closed. The class matters as much as the message,
    /// because the ordering rule below is expressed in terms of these.
    /// </summary>
    /// <remarks>
    /// <b>The order of this enum is the order the ladder walks, and that is the
    /// design rather than tidiness.</b> Hardware is tested before any licence so
    /// that an operator whose radio physically cannot do the thing is told so,
    /// and is <b>never invited to buy a licence that would do nothing for
    /// them</b> — that is the actively harmful error and the ordering is the only
    /// thing preventing it. Resources come last for the mirror reason: a radio
    /// without the licence should hear about the licence, not about antennas.
    /// Each reason is only worth saying once the more fundamental ones pass.
    /// </remarks>
    public enum CapabilityBlock
    {
        /// <summary>Nothing is in the way. The capability is available now.</summary>
        None = 0,

        /// <summary>No radio. Transient, says nothing about the capability.</summary>
        NotConnected,

        /// <summary>Operator context missing — no slice selected. Transient.</summary>
        NoSlice,

        /// <summary>
        /// The hardware cannot do it, ever, on this radio. A local permanent
        /// fact, so it is safe to state plainly and needs no network round trip.
        /// </summary>
        Hardware,

        /// <summary>
        /// The radio has not told us about the licence. <b>A third state, not a
        /// synonym for disabled.</b> A licence we have never been told about is
        /// not evidence of anything, so nothing that REFUSES or REMOVES may act
        /// on it.
        /// </summary>
        LicenceNotReported,

        /// <summary>The radio reported the licence and it is positively off.</summary>
        LicenceDisabled,

        /// <summary>
        /// Something the operator can go and change — antennas, slices. Phrased
        /// as an instruction rather than a refusal.
        /// </summary>
        Resource,

        /// <summary>Wrong mode for the feature. Operator-fixable, like a resource.</summary>
        Mode,
    }

    /// <summary>
    /// The answer for one capability on one radio at one moment: whether it is
    /// available, and if not, which class of reason closed it and what to say.
    /// </summary>
    public readonly struct CapabilityVerdict
    {
        public CapabilityVerdict(RadioCapability capability, CapabilityBlock block, string message)
        {
            Capability = capability;
            Block = block;
            Message = message ?? string.Empty;
        }

        public RadioCapability Capability { get; }

        public CapabilityBlock Block { get; }

        /// <summary>
        /// What to say. <b>Empty when available</b>, matching the convention
        /// <see cref="FlexBase.DiversityGateMessage"/> established — do not
        /// invent a second convention for "nothing to report".
        /// </summary>
        public string Message { get; }

        public bool Allowed => Block == CapabilityBlock.None;

        /// <summary>
        /// True when this verdict is about a licence, so a test can assert that
        /// a radio refused for HARDWARE reasons was never told about a
        /// subscription. That assertion is the one most worth having.
        /// </summary>
        public bool IsAboutLicence
            => Block == CapabilityBlock.LicenceDisabled || Block == CapabilityBlock.LicenceNotReported;

        /// <summary>
        /// True when the operator can do something about it themselves, which is
        /// the difference between a refusal and an instruction.
        /// </summary>
        public bool IsOperatorFixable
            => Block == CapabilityBlock.Resource || Block == CapabilityBlock.Mode
               || Block == CapabilityBlock.NoSlice;
    }

    /// <summary>
    /// Hardware facts that are decidable from the model string alone — pure,
    /// static, and testable without a radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists so the hardware matrix can be a table test.</b> Every
    /// hardware predicate used to read <c>theRadio.Model</c> through the live
    /// connection, which meant it returned false with no radio attached and the
    /// matrix could not be exercised without one radio per model — nine models
    /// against one bench. A predicate that cannot be tested is not merely
    /// unverified, it is unfalsifiable, which is how a wrong one survived with a
    /// confident comment beside it (#729).
    /// </para>
    /// <para>
    /// <b>Not everything belongs here.</b> Where the radio reports a capability
    /// itself — <c>DiversityIsAllowed</c>, the <c>RigCaps</c> table — that
    /// report is better evidence than a model table we maintain, and replacing
    /// it with a string match would be a regression. Only put a fact here when
    /// the model string genuinely is the authority.
    /// </para>
    /// </remarks>
    public static class CapabilityHardware
    {
        /// <summary>
        /// True when the radio's DSP can run the advanced noise-reduction family
        /// — NRF, NRS and RNN.
        /// </summary>
        /// <remarks>
        /// <b>8000-series and Aurora only, and this is a vendor fact rather than
        /// our inference.</b> FlexRadio's SmartSDR v4.1.3 release notes say
        /// "Only available on 8000 Series Radios" of NRF, NRS and RNN in three
        /// separate sentences, and mark all three footnote 1 (Aurora,
        /// FLEX-8000); NRL, ANFL and ANFT carry footnote 1,2 and so also run on
        /// the 6000s. The AU-520-only feature is Enhanced Signal Clarity, which
        /// needs dual SCUs — <b>not</b> RNN, and conflating the two is what made
        /// an earlier predicate exclude the AU-510 (#729). Working:
        /// JJFlex-private/planning/research/flexradio-noise-mitigation-model-matrix.md
        /// </remarks>
        public static bool SupportsAdvancedNoiseReduction(string? model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            return model.StartsWith("FLEX-8", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("AU-5", StringComparison.OrdinalIgnoreCase);
        }
    }
}
