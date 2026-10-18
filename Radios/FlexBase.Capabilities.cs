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
        /// sent.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>DO NOT SPEAK FROM THIS EVENT.</b> As of 2026-10-17 all three roads
        /// to the advanced-NR switches — the noise layer, the DSP expander's
        /// checkboxes and the Radio menu — ask
        /// <see cref="CapabilityWriteRefusal"/> before they announce anything,
        /// and each says the refusal itself. A subscriber here that also spoke
        /// would therefore say it twice, and a doubled announcement is a defect
        /// this project has paid for before (#521).
        /// </para>
        /// <para>
        /// <b>What it is for:</b> the trace, the tests, and noticing a road that
        /// did NOT ask. A new surface that writes one of these switches without
        /// consulting the ladder still gets its write refused — that is the
        /// point of gating the setter — and this event is how that shows up as
        /// something other than silence. If a central speaker is ever wanted,
        /// the three roads must stop speaking for themselves in the same change.
        /// </para>
        /// </remarks>
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

            /// <summary>
            /// Lexicon key for the SHORT licence refusal — a key layer cannot
            /// speak a paragraph. Falls back to
            /// <see cref="LicenceDisabledFallbackText"/> when absent.
            /// </summary>
            public string? LicenceDisabledShortKey { get; init; }

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
                LicenceDisabledShortKey = "audio.nr.filter_unsubscribed",
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
                LicenceDisabledShortKey = "audio.nr.spectral_unsubscribed",
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
                LicenceDisabledShortKey = "audio.nr.neural_unsubscribed",
                LicenceDisabledLongText = AdvancedNrLicenceProse,
                ModeRequirement = NoiseReductionModeRequirement,
            },
            // DIVERSITY CARRIES NO LICENCE. Verified at the radio 2026-10-18
            // (#745): on an 8600 reporting div_esc DISABLED, the radio built
            // the diversity pair anyway -- slice 0 and a radio-created slice 3,
            // cross-referenced as parent and child. Minutes later ESC was
            // refused on that same live pair with the same licence state.
            //
            // The boundary is WHEN the feature shipped, per Noel and per
            // FlexRadio's 4.2.20 note ("Radios licensed for SmartSDR v3 will
            // retain all of the prior SmartSDR v3 features when running
            // SmartSDR v4"): diversity is v3 and Basic, ESC is v4 and Plus.
            // div_esc is a single licence item whose NAME straddles that
            // boundary, so any gate keyed on it alone must be wrong about one
            // of the two features it names. It was wrong about this one, and
            // the cost was telling a two-SCU owner to buy what he owned.
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
                Resources = DiversityResourceRequirement,
            },
            // ESC IS THE LICENSED HALF, and it needs a ladder of its OWN.
            //
            // NativeMenuBar.EscGateMessage used to return null on
            // DiversityReady, reasoning that reusing diversity's ladder meant
            // "the two can never drift into disagreeing about the same radio".
            // Normally right; wrong here, because they MUST disagree on exactly
            // this term. Merging them back would strip ESC's licence gate the
            // moment diversity's was removed -- which is precisely the edit
            // that happened above. Keep them separate.
            //
            // Diversity being currently OFF is deliberately NOT a rung here.
            // The ESC dialog opens and says "turn diversity on first", because
            // that is an instruction the operator can act on, while a menu item
            // that vanishes is not.
            new CapabilityDescriptor
            {
                Capability = RadioCapability.Esc,
                Name = "Enhanced Signal Clarity",
                NotConnectedText = "Radio not ready",
                NoSliceText = "Select a slice",
                Hardware = r => r.theRadio?.DiversityIsAllowed == true,
                HardwareShortKey = null,
                HardwareLongText = "Model lacks the two receivers ESC needs",
                Licence = r => r.theRadio?.FeatureLicense?.LicenseFeatDivEsc,
                LicenceNotReportedText = "ESC license status pending",
                // UNREACHABLE TODAY, AND THE WORDING IS WRONG EITHER WAY --
                // #752. The selection above prefers FlexLib's
                // FeatureGatedMessage whenever a row carries no
                // LicenceDisabledLongText, and #742 records that FlexLib always
                // says something for a disabled licence, so this string is dead.
                //
                // An earlier comment here argued that deferring to FlexRadio was
                // CORRECT for ESC, since we have no PC-side alternative to point
                // at. #752 rules otherwise, and for a better reason: a
                // plus-gated feature is unlocked by SmartSDR+ OR SmartSDR+ Early
                // Access, and the vendor string names only one of the two. So it
                // is not merely not-ours, it is INCOMPLETE -- it would send an
                // operator to buy the narrower of two things that work.
                //
                // #752 names this row and the EvaluateCapability preference as
                // sites to change; the sentence itself is Noel's under #720.
                // Do not edit this literal expecting the operator to hear it.
                LicenceDisabledFallbackText = "Subscribe to SmartSDR+ to enable Enhanced Signal Clarity",
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

            // AvailableSlices IS NOT READ HERE ANY MORE, and MaxSlices never
            // was -- #765. Both come from the radio's discovery broadcast, and
            // both degrade. MaxSlices came back 0 from a real remote 6300 over
            // SmartLink (2026-10-10). AvailableSlices was measured on the 8600
            // on 2026-10-18: it tracked 4 -> 1 as three slices came up, then
            // FROZE at 1 while a slice was released and while the radio itself
            // created a fourth, with AnnounceSliceCensus reporting 3/4 and then
            // 2/4 correctly from the same radio in the same seconds.
            //
            // So capacity is computed from the two values that demonstrably
            // track: the model-derived maximum, which the code already says to
            // prefer, and the live slice list -- every slice on the radio, not
            // just ours, because on a MultiFlex radio another station's slices
            // occupy the same capacity.
            //
            // ONE free slice, not two. Measured: diversity engaged with three
            // slices live on a four-slice radio, and the radio created the
            // partner itself. Noel's recollection was two, offered as "I
            // think", and it was deliberately not enforced on that basis --
            // writing an unverified number into a gate is the same machine that
            // kept #745 alive, one term over.
            // CAPACITY IS A PRECONDITION FOR BUILDING THE PAIR, NOT FOR
            // KEEPING IT. Once diversity is on, the pair is OCCUPYING the
            // capacity it needed -- on a four-slice radio that is four of four
            // live and nothing free. Testing for free space then closes the
            // gate, and DiversityReady governs the menu item that both enables
            // AND disables diversity, so the operator is stranded with the
            // feature on and no road back. Caught 2026-10-18 before it reached
            // the radio, on a bench session that had left a pair running.
            //
            // Asked of the radio's own pair state first and our cached flag
            // second. DiversityChild is radio-written, so it answers even when
            // DiversityOn is only reflecting something we wrote; the cached
            // flag still matters, because an operator who turned it on deserves
            // to be able to turn it off whether or not the radio agreed.
            if (radio.SliceList != null)
            {
                foreach (Slice s in radio.SliceList)
                {
                    if (s.DiversityChild || s.DiversityOn) return null;
                }
            }
            if (rig.DiversityOn) return null;

            int free = rig.TotalMaxSlices - (radio.SliceList?.Count ?? 0);
            return free < 1 ? "Free up a slice for the diversity pair" : null;
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

            // Gather, then decide. The ladder ORDER lives in DecideBlock, which
            // is pure and takes no radio - that is what lets the ordering and
            // the three-state asymmetry be table tests on a bench holding one
            // radio. This method's remaining job is to read the inputs off the
            // rig and to pick the wording for whatever block came back.
            bool connected = theRadio != null;
            bool requiresSlice = d.NoSliceText != null;
            bool hasHardwareRequirement = d.Hardware != null;

            bool hardwareSupported = !hasHardwareRequirement || (connected && d.Hardware!(this));
            LicenceState licence = connected ? LicenceStateOf(d) : LicenceState.NotApplicable;
            string? resourceWhy = connected && d.Resources != null ? d.Resources(this) : null;
            string? modeWhy = connected && d.ModeRequirement != null ? d.ModeRequirement(this) : null;

            CapabilityBlock block = DecideBlock(
                connected, requiresSlice, HasActiveSlice,
                hasHardwareRequirement, hardwareSupported,
                licence, resourceWhy == null, modeWhy == null);

            if (block == CapabilityBlock.NotConnected)
                return new CapabilityVerdict(capability, block, d.NotConnectedText);

            if (block == CapabilityBlock.NoSlice)
                return new CapabilityVerdict(capability, block, d.NoSliceText ?? string.Empty);

            if (block == CapabilityBlock.Hardware)
            {
                string message = wording == CapabilityWording.Long || d.HardwareShortKey == null
                    ? d.HardwareLongText
                    : Lexicon.Get(d.HardwareShortKey);
                return new CapabilityVerdict(capability, CapabilityBlock.Hardware, message);
            }

            if (block == CapabilityBlock.LicenceNotReported)
            {
                // Never reported is a THIRD STATE. A licence we have never been
                // told about is not evidence of anything - the radio may simply
                // not have sent its feature list yet - so the message may be
                // empty and the caller is expected to let the write through.
                return new CapabilityVerdict(
                    capability, CapabilityBlock.LicenceNotReported, d.LicenceNotReportedText ?? string.Empty);
            }

            if (block == CapabilityBlock.LicenceDisabled)
            {
                var feature = d.Licence?.Invoke(this);
                {
                    // The radio's own sentence about its own subscription beats
                    // ours - EXCEPT where we hold long prose that names a
                    // working alternative, because the radio cannot know this
                    // application has a PC-side noise reducer and the operator
                    // being told what DOES work is worth more than the vendor's
                    // phrasing of what does not. Descriptors with no long text
                    // of their own keep preferring the radio, which is how
                    // diversity behaved before this class existed.
                    string message =
                        wording == CapabilityWording.Long && d.LicenceDisabledLongText != null
                            ? d.LicenceDisabledLongText
                            : feature?.FeatureGatedMessage
                              ?? (wording == CapabilityWording.Short && d.LicenceDisabledShortKey != null
                                  ? Lexicon.Get(d.LicenceDisabledShortKey)
                                  : d.LicenceDisabledFallbackText);
                    return new CapabilityVerdict(capability, CapabilityBlock.LicenceDisabled, message);
                }
            }

            if (block == CapabilityBlock.Resource)
            {
                return new CapabilityVerdict(capability, CapabilityBlock.Resource, resourceWhy ?? string.Empty);
            }

            if (block == CapabilityBlock.Mode)
            {
                return new CapabilityVerdict(capability, CapabilityBlock.Mode, modeWhy ?? string.Empty);
            }

            return new CapabilityVerdict(capability, CapabilityBlock.None, string.Empty);
        }

        /// <summary>
        /// The three states a licence can be in, plus the fourth that means the
        /// capability is not licensed at all.
        /// </summary>
        /// <remarks>
        /// <b><see cref="NotReported"/> is the one that matters.</b> It is not a
        /// synonym for <see cref="Disabled"/>: a radio that has not sent its
        /// feature list yet has told us nothing, and treating silence as a "no"
        /// is how an application takes away a feature the operator paid for.
        /// Keeping it as a named state rather than a bool is what makes the
        /// asymmetry impossible to collapse by accident.
        /// </remarks>
        public enum LicenceState
        {
            /// <summary>This capability carries no licence requirement.</summary>
            NotApplicable = 0,

            /// <summary>The radio has not told us. Not evidence of anything.</summary>
            NotReported,

            /// <summary>The radio reported it, and it is off.</summary>
            Disabled,

            /// <summary>The radio reported it, and it is on.</summary>
            Enabled,
        }

        private LicenceState LicenceStateOf(CapabilityDescriptor d)
        {
            if (d.Licence == null) return LicenceState.NotApplicable;
            var feature = d.Licence(this);
            if (feature == null) return LicenceState.NotReported;
            return feature.FeatureEnabled ? LicenceState.Enabled : LicenceState.Disabled;
        }

        /// <summary>
        /// The ladder, as a pure function. <b>This method IS the ordering</b>,
        /// and the ordering is the design.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why it is pure.</b> Every input arrives as an argument, so the
        /// order and the three-state asymmetry can be asserted exhaustively
        /// with no radio, no connection and no mocking. The predicate that
        /// decides a hardware fact went the same way in #729, for the same
        /// reason: a decision that can only be exercised through a live radio
        /// is a decision nobody exercises, and a wrong one then survives behind
        /// a confident comment.
        /// </para>
        /// <para>
        /// <b>Hardware before any licence.</b> A radio that physically cannot
        /// do the thing must be told so, and must never be invited to buy a
        /// licence that would do nothing for it. That is the actively harmful
        /// error and this ordering is the only thing preventing it.
        /// </para>
        /// <para>
        /// <b>Resources and mode last.</b> A radio without the licence should
        /// hear about the licence, not about antennas. Each reason is only
        /// worth saying once the more fundamental ones have passed.
        /// </para>
        /// </remarks>
        internal static CapabilityBlock DecideBlock(
            bool connected,
            bool requiresSlice,
            bool sliceSelected,
            bool hasHardwareRequirement,
            bool hardwareSupported,
            LicenceState licence,
            bool resourcesSatisfied,
            bool modeSatisfied)
        {
            if (!connected) return CapabilityBlock.NotConnected;
            if (requiresSlice && !sliceSelected) return CapabilityBlock.NoSlice;

            // Not reported is reported BEFORE disabled and after hardware, and
            // it is a distinct answer rather than a flavour of refusal. What a
            // caller does with it is the caller's policy: a status readout may
            // say "pending", and anything that refuses or removes must let it
            // through.
            if (hasHardwareRequirement && !hardwareSupported) return CapabilityBlock.Hardware;

            // Not reported is reported BEFORE disabled and after hardware, and
            // it is a distinct answer rather than a flavour of refusal. What a
            // caller does with it is the caller's policy: a status readout may
            // say "pending", and anything that refuses or removes must let it
            // through.
            if (licence == LicenceState.NotReported) return CapabilityBlock.LicenceNotReported;
            if (licence == LicenceState.Disabled) return CapabilityBlock.LicenceDisabled;

            if (!resourcesSatisfied) return CapabilityBlock.Resource;
            if (!modeSatisfied) return CapabilityBlock.Mode;

            return CapabilityBlock.None;
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
        /// Whether this class of reason actually stops a write.
        /// </summary>
        /// <remarks>
        /// <b>One predicate, so the gate and every presentation that explains it
        /// cannot disagree.</b> A key layer that announced a refusal the setter
        /// then let through would be #725 inverted — the announcement and the
        /// radio saying different things — which is the whole defect, not a
        /// smaller version of it.
        /// </remarks>
        internal static bool BlocksWrite(CapabilityBlock block)
            => block == CapabilityBlock.Hardware
               || block == CapabilityBlock.LicenceDisabled
               || block == CapabilityBlock.Resource;

        /// <summary>
        /// The refusal a presentation should speak when the operator presses
        /// this capability's control, or null when the write would go through.
        /// </summary>
        /// <remarks>
        /// Use this rather than <see cref="CapabilityRefusal"/> wherever the
        /// answer is about to be SPOKEN in response to a press: it agrees with
        /// the setter by construction, including about mode, which the ladder
        /// reports but the write gate currently treats as advisory.
        /// </remarks>
        public string? CapabilityWriteRefusal(
            RadioCapability capability, CapabilityWording wording = CapabilityWording.Short)
        {
            var verdict = EvaluateCapability(capability, wording);
            if (!BlocksWrite(verdict.Block)) return null;
            return string.IsNullOrEmpty(verdict.Message) ? null : verdict.Message;
        }

        /// <summary>
        /// True when this radio cannot use the capability at all — its hardware
        /// lacks it, or its licence positively says no.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately narrower than "refused".</b> A missing antenna, the
        /// wrong mode and a slice not yet selected are all things the operator
        /// can change in the next ten seconds, so they are not facts about the
        /// radio and must not make a control disappear from a list. An
        /// unreported licence is excluded for the same reason it never refuses:
        /// not knowing is not a no.
        /// </remarks>
        public bool CapabilityIsClosedOnThisRadio(RadioCapability capability)
        {
            var block = EvaluateCapability(capability).Block;
            return block == CapabilityBlock.Hardware || block == CapabilityBlock.LicenceDisabled;
        }

        /// <summary>
        /// Why this radio cannot use the capability, as a block class, or
        /// <see cref="CapabilityBlock.None"/> when it can.
        /// </summary>
        public CapabilityBlock CapabilityClosedReason(RadioCapability capability)
        {
            var block = EvaluateCapability(capability).Block;
            return block == CapabilityBlock.Hardware || block == CapabilityBlock.LicenceDisabled
                ? block
                : CapabilityBlock.None;
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

            // MODE IS ADVISORY HERE, AND DELIBERATELY NOT A REFUSAL YET.
            //
            // The ladder reports it so a status readout can explain itself, but
            // the write still goes through. The belief that these features are
            // unavailable in CW or FM comes from ONE status builder in this
            // codebase and has never been checked against a radio - and the
            // cost of being wrong is asymmetric. If the belief is right we send
            // a command the radio ignores, which is what happened before this
            // class existed. If it is wrong and we refuse, we have taken a
            // working feature away in a whole class of modes, which is exactly
            // the defect #726 exists to prevent, rebuilt by the fix for it.
            //
            // One reading at the radio settles it: turn NR on in CW and see
            // whether the radio reports it on. Until then the gate does not act
            // on a belief it cannot source.
            // Routed through the one predicate so a presentation asking "will
            // this be refused?" gets the same answer the setter acts on.
            if (!BlocksWrite(verdict.Block))
            {
                Tracing.TraceLine(
                    "CapabilityWrite allowed, block is advisory: " + capability
                    + " block=" + verdict.Block + " msg=" + verdict.Message, TraceLevel.Verbose);
                return false;
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
