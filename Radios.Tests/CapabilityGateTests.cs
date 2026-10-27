using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The capability gate's own guarantees: every capability has a row, and the
    /// advanced-NR hardware matrix matches what FlexRadio documents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this file exists at all.</b> On 2026-10-17 this suite ran 4,499
    /// tests and passed identically before and after a predicate that excluded
    /// an entire radio model from a feature it supports (#729). Nothing asserted
    /// on the hardware matrix, so nothing could notice. A green suite that is
    /// silent about an area is not coverage of that area, and the wrong comment
    /// beside the wrong predicate read as deliberate for exactly as long as
    /// nothing contradicted it.
    /// </para>
    /// <para>
    /// <b>The matrix is testable because the predicate is pure.</b>
    /// <see cref="CapabilityHardware.SupportsAdvancedNoiseReduction"/> takes a
    /// model string rather than reaching through a live connection, which is the
    /// whole reason nine models can be asserted on a bench that holds one radio.
    /// Do not "simplify" it back into a property that reads the radio.
    /// </para>
    /// </remarks>
    public class CapabilityGateTests
    {
        /// <summary>
        /// The acceptance test for the whole design, enforced rather than
        /// merely written down: adding a gated feature is one row in the table,
        /// and forgetting the row fails here instead of shipping an ungated
        /// capability that looks gated.
        /// </summary>
        [Fact]
        public void EveryCapabilityHasADescriptor()
        {
            var declared = Enum.GetValues<RadioCapability>();
            Assert.NotEmpty(declared);

            IReadOnlyList<RadioCapability> gated = FlexBase.GatedCapabilities;

            foreach (RadioCapability capability in declared)
            {
                Assert.Contains(capability, gated);
            }

            // And no row for a capability that does not exist, so the table
            // cannot quietly accumulate dead entries either.
            Assert.Equal(declared.Length, gated.Count);
            Assert.Equal(gated.Count, gated.Distinct().Count());
        }

        /// <summary>
        /// The measured split: plain diversity is hardware-only, while ESC
        /// alone resolves the radio's <c>div_esc</c> licence item.
        /// </summary>
        /// <remarks>
        /// This is the re-merge detector. On 2026-10-18 an 8600 built a
        /// diversity pair while <c>div_esc</c> was reported disabled, then the
        /// same radio refused ESC. A tidy shared ladder would recreate the
        /// defect by making those two measured answers identical again.
        /// </remarks>
        [Fact]
        public void TheMeasuredDiversityEscSplitCannotBeMergedBackIntoOneLadder()
        {
            var diversity = FlexBase.DescriptorFor(RadioCapability.DiversityReception);
            var esc = FlexBase.DescriptorFor(RadioCapability.Esc);

            Assert.True(diversity.Licence == null,
                "plain diversity worked on the 8600 while div_esc was disabled; it must carry no licence rung");
            Assert.NotNull(esc.Licence);

            // They really do share the permanent hardware question and the
            // resource question. The licence term is the one measured split.
            Assert.Equal(diversity.Hardware, esc.Hardware);
            Assert.Equal(diversity.Resources, esc.Resources);

            var marker = new Feature("div_esc", false, FeatureStatusReason.Plus);
            FlexBase rig = OfflineDiversityRig(sliceCount: 3, marker);
            try
            {
                Assert.Same(marker, esc.Licence!(rig));
            }
            finally
            {
                DisposeOfflineRig(rig);
            }
        }

        /// <summary>
        /// Discovery-broadcast slice counts are observations, not capacity
        /// authority, and neither may close the diversity resource rung.
        /// </summary>
        [Fact]
        public void TheDiversityResourceRungDoesNotReadDiscoverySliceCounts()
        {
            string source = ReadRepoFile("Radios/FlexBase.Capabilities.cs");
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.ValueText == "DiversityResourceRequirement");
            var reads = method.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Select(m => m.Name.Identifier.ValueText)
                .ToList();

            Assert.DoesNotContain("AvailableSlices", reads);
            Assert.DoesNotContain("MaxSlices", reads);
        }

        /// <summary>
        /// One free slice passes and zero fails. One was measured at the radio;
        /// two was a recollection and must never become the gate.
        /// </summary>
        [Fact]
        public void DiversityNeedsOneMeasuredFreeSliceNotTwo()
        {
            FlexBase oneFree = OfflineDiversityRig(sliceCount: 3);
            FlexBase noneFree = OfflineDiversityRig(sliceCount: 4);
            try
            {
                Assert.Null(FlexBase.DiversityResourceRequirement(oneFree));
                Assert.Equal("Free up a slice for the diversity pair",
                    FlexBase.DiversityResourceRequirement(noneFree));
            }
            finally
            {
                DisposeOfflineRig(oneFree);
                DisposeOfflineRig(noneFree);
            }
        }

        /// <summary>
        /// The rendered operator report must describe owned plain diversity
        /// independently from licensed ESC on the same radio.
        /// </summary>
        [Fact]
        public void FeatureAvailabilityDoesNotCallOwnedDiversityUnsubscribed()
        {
            var divEsc = new Feature("div_esc", false, FeatureStatusReason.Plus);
            FlexBase rig = OfflineDiversityRig(sliceCount: 3, divEsc);
            try
            {
                string[] lines = rig.BuildFeatureAvailabilityText()
                    .Split(new[] { Environment.NewLine }, StringSplitOptions.None);
                string diversity = Assert.Single(lines.Where(l => l.StartsWith("Diversity:", StringComparison.Ordinal)));
                string esc = Assert.Single(lines.Where(l => l.StartsWith("ESC:", StringComparison.Ordinal)));

                Assert.Equal("Diversity: disabled - diversity ready", diversity);
                Assert.DoesNotContain("unsubscribed", diversity, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("ESC: unsubscribed - Subscribe to SmartSDR+ to use this feature!", esc);
            }
            finally
            {
                DisposeOfflineRig(rig);
            }
        }

        /// <summary>
        /// The standing first-verdict trace names all thirteen vendored items,
        /// including absence, enabled state, and the radio's reason code.
        /// </summary>
        [Fact]
        public void LicenseInventoryNamesAllThirteenItemsAndPreservesProtocolReasonCodes()
        {
            var divEsc = new Feature("div_esc", false, FeatureStatusReason.Plus);
            FlexBase rig = OfflineDiversityRig(sliceCount: 3, divEsc);
            try
            {
                string inventory = rig.BuildLicenseFeatureInventoryTrace();
                string[] names =
                {
                    "alpha", "auto_tune", "digital_voice_keyer", "div_esc", "multiflex",
                    "navtex_wf", "noise_floor", "noise_reduction", "smartlink", "wfp",
                    "rapidm_logging", "panadapter_visuals", "filter_preset_conf",
                };

                Assert.All(names, name => Assert.Contains(name + "=[", inventory, StringComparison.Ordinal));
                Assert.Equal(13, inventory.Split("=[", StringSplitOptions.None).Length - 1);
                Assert.Contains("div_esc=[reported=true enabled=false reason=plus]", inventory, StringComparison.Ordinal);
                Assert.Contains("auto_tune=[reported=false enabled=n/a reason=n/a]", inventory, StringComparison.Ordinal);
            }
            finally
            {
                DisposeOfflineRig(rig);
            }
        }

        /// <summary>
        /// Every licence item FlexLib parses is either read by a row in the
        /// capability table or named here as deliberately absent, with why.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The guard #746 asked for.</b> #726 gated the capabilities its own
        /// table named, and NRL, ANFL and ANFT were never in it - three licensed
        /// switches the class fix could not reach, because the class was
        /// defined as "the rows". This test defines it as "the items the radio
        /// can report", so a fourteenth item in a FlexLib upgrade fails here
        /// until someone decides which list it belongs on.
        /// </para>
        /// <para>
        /// The mapping is OBSERVED, not declared: each item gets a distinct
        /// marker and each row's licence accessor is asked which marker it
        /// returns. A row reading the wrong item therefore shows up as a
        /// different coverage set, not as a passing test.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryLicenceItemFlexLibParsesIsGatedOrDeliberatelyAbsent()
        {
            // Each reason is the #726 membership test: does the radio refuse
            // in a way the operator can perceive, and do we have a road to it?
            var deliberatelyAbsent = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LicenseFeatAlpha"] = "firmware alpha programme access; no feature road in this application",
                ["LicenseFeatSmartlink"] = "fails loudly at connect; a client gate adds nothing",
                ["LicenseFeatMultiflex"] = "fails loudly at connect; a client gate adds nothing",
                ["LicenseFeatDVK"] = "the radio's Digital Voice Keyer has no road here, by decision",
                ["LicenseFeatNAVTEX"] = "NAVTEX waveform; no road in this application",
                ["LicenseFeatWFP"] = "waveform programme; no road in this application",
                ["LicenseFeatNoiseFloor"] = "panadapter noise-floor display; no road in this application",
                ["LicenseFeatRapidmLogging"] = "RapidM logging; no road in this application",
                ["LicenseFeatPanadapterVisuals"] = "panadapter display markers; no road in this application",
                ["LicenseFeatFilterPresetConf"] = "radio-side filter preset configuration; no road in this application",
            };

            var items = typeof(FeatureLicense).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.PropertyType == typeof(Feature)
                            && p.Name.StartsWith("LicenseFeat", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(13, items.Count);

            FlexBase rig = OfflineDiversityRig(sliceCount: 1);
            try
            {
                var markers = new Dictionary<Feature, string>(ReferenceEqualityComparer.Instance);
                foreach (var item in items)
                {
                    var marker = new Feature(item.Name, false, FeatureStatusReason.Plus);
                    item.SetValue(rig.theRadio!.FeatureLicense, marker);
                    markers.Add(marker, item.Name);
                }

                var gated = new HashSet<string>(StringComparer.Ordinal);
                foreach (RadioCapability capability in FlexBase.GatedCapabilities)
                {
                    var licence = FlexBase.DescriptorFor(capability).Licence;
                    if (licence == null) continue;
                    Feature? read = licence(rig);
                    Assert.True(read != null && markers.ContainsKey(read),
                        capability + " reads a licence item that is not one of FlexLib's LicenseFeat properties");
                    gated.Add(markers[read!]);
                }

                foreach (var item in items)
                {
                    Assert.True(gated.Contains(item.Name) || deliberatelyAbsent.ContainsKey(item.Name),
                        item.Name + " is neither read by a CapabilityTable row nor listed as deliberately absent");
                    Assert.False(gated.Contains(item.Name) && deliberatelyAbsent.ContainsKey(item.Name),
                        item.Name + " is gated AND listed as deliberately absent; take it off the list");
                }

                // The absent list cannot outlive a rename in FlexLib either.
                Assert.All(deliberatelyAbsent.Keys, name =>
                    Assert.Contains(items, p => p.Name == name));

                // And the three #746 switches really are on noise_reduction.
                Assert.Contains("LicenseFeatNoiseReduction", gated);
            }
            finally
            {
                DisposeOfflineRig(rig);
            }
        }

        /// <summary>
        /// #746: FFT auto-notch refuses the write when the radio reports
        /// <c>noise_reduction</c> disabled.
        /// </summary>
        /// <remarks>
        /// Through the setter, the road every surface reaches the radio by -
        /// the noise layer's A key included. Measured 2026-10-27: an unlicensed
        /// 8600 answered <c>anft=1</c> and <c>anft=0</c> with 0x50004001, its
        /// licence refusal, so this gate agrees with the radio.
        /// </remarks>
        [Fact]
        public void FftAutoNotchRefusesTheWriteWhenTheLicenceIsDisabled()
        {
            FlexBase rig = OfflineDiversityRig(sliceCount: 1);
            try
            {
                rig.theRadio!.FeatureLicense.LicenseFeatNoiseReduction =
                    new Feature("noise_reduction", false, FeatureStatusReason.Plus);

                var refused = new List<RadioCapability>();
                rig.CapabilityWriteRefused += (_, e) => refused.Add(e.Verdict.Capability);

                rig.AutoNotchFFT = FlexBase.OffOnValues.on;

                Assert.Equal(new[] { RadioCapability.AutoNotchFft }, refused);
                Assert.False(string.IsNullOrWhiteSpace(rig.CapabilityWriteRefusal(RadioCapability.AutoNotchFft)));
                Assert.True(rig.CapabilityIsClosedOnThisRadio(RadioCapability.AutoNotchFft));
            }
            finally
            {
                DisposeOfflineRig(rig);
            }
        }

        /// <summary>
        /// The positive control for the test above: an enabled licence and an
        /// unreported one both let FFT auto-notch through.
        /// </summary>
        [Fact]
        public void FftAutoNotchPassesWhenLicensedOrNotYetReported()
        {
            foreach (Feature? licence in new[] { new Feature("noise_reduction", true, FeatureStatusReason.Plus), null })
            {
                FlexBase rig = OfflineDiversityRig(sliceCount: 1);
                try
                {
                    rig.theRadio!.FeatureLicense.LicenseFeatNoiseReduction = licence;
                    Assert.False(rig.RefuseCapabilityWrite(RadioCapability.AutoNotchFft),
                        "refused with licence " + (licence == null ? "unreported" : "enabled"));
                    Assert.Null(rig.CapabilityWriteRefusal(RadioCapability.AutoNotchFft));
                }
                finally
                {
                    DisposeOfflineRig(rig);
                }
            }
        }

        /// <summary>
        /// LEGACY NR (NRL) AND LEGACY AUTO-NOTCH (ANFL) ARE NOT LICENCE-GATED.
        /// Do not re-gate them from FlexRadio's documents.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Measured 2026-10-27 (#746)</b> on an unlicensed 8600 reporting
        /// <c>noise_reduction</c> reported=true enabled=false reason=plus, with
        /// raw <c>slice set 0 &lt;cmd&gt;=1/0</c> and the radio's reply traced:
        /// <c>lms_nr</c> and <c>lms_anf</c> were ACCEPTED (0x00000000) and the
        /// operator heard both change the audio, while <c>anft</c>,
        /// <c>speex_nr</c>, <c>nrf</c> and <c>rnnoise</c> were REFUSED with
        /// 0x50004001.
        /// </para>
        /// <para>
        /// FlexRadio's licence documents group NRL and ANFL with ANFT as
        /// SmartSDR+, and the first #746 build gated all three on that basis,
        /// which would have taken two working features away from every
        /// unlicensed radio. The documents were wrong for these two, as they
        /// were for diversity under #745. This test holds every surface to the
        /// measurement: no capability, no refused write, no "unsubscribed" in
        /// Feature Availability, and no gated road in the menu or the DSP
        /// expander.
        /// </para>
        /// </remarks>
        [Fact]
        public void LegacyNrAndLegacyAutoNotchAreNotLicenceGated()
        {
            // No capability exists for them, so nothing can gate them.
            string[] names = Enum.GetNames<RadioCapability>();
            Assert.DoesNotContain("NoiseReductionLegacy", names);
            Assert.DoesNotContain("AutoNotchLegacy", names);

            FlexBase rig = OfflineDiversityRig(sliceCount: 1);
            try
            {
                rig.theRadio!.FeatureLicense.LicenseFeatNoiseReduction =
                    new Feature("noise_reduction", false, FeatureStatusReason.Plus);

                // The writes are not refused on a radio whose licence is off.
                var refused = new List<RadioCapability>();
                rig.CapabilityWriteRefused += (_, e) => refused.Add(e.Verdict.Capability);
                rig.NoiseReductionLegacy = FlexBase.OffOnValues.on;
                rig.AutoNotchLegacy = FlexBase.OffOnValues.on;
                Assert.Empty(refused);

                // Feature Availability agrees: neither line is called
                // unsubscribed, or tied to the licence at all.
                string[] lines = rig.BuildFeatureAvailabilityText()
                    .Split(new[] { Environment.NewLine }, StringSplitOptions.None);
                string nrl = Assert.Single(lines.Where(l => l.StartsWith("Noise Reduction (NRL):", StringComparison.Ordinal)));
                string anfl = Assert.Single(lines.Where(l => l.StartsWith("Auto Notch (ANFL):", StringComparison.Ordinal)));
                Assert.DoesNotContain("unsubscribed", nrl, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("unsubscribed", anfl, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("SmartSDR", nrl, StringComparison.Ordinal);
                Assert.DoesNotContain("SmartSDR", anfl, StringComparison.Ordinal);

                // Positive control: the same radio and the same text DO call
                // ANFT unsubscribed, so the absence above is not a blind spot.
                Assert.Contains(lines, l => l.StartsWith("Auto Notch (ANFT): unsubscribed", StringComparison.Ordinal));
            }
            finally
            {
                DisposeOfflineRig(rig);
            }

            // And no presentation road asks a ladder about them.
            string menu = ReadRepoFile("JJFlexWpf/NativeMenuBar.cs");
            foreach (string label in new[] { "Legacy NR", "Legacy Auto-Notch" })
            {
                int at = menu.IndexOf("ToggleDSP(\"" + label + "\"", StringComparison.Ordinal);
                Assert.True(at >= 0, "no ToggleDSP call for " + label);
                int end = menu.IndexOf(");", at, StringComparison.Ordinal);
                Assert.DoesNotContain("RadioCapability.", menu.Substring(at, end - at));
            }

            string panel = ReadRepoFile("JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs");
            foreach (string control in new[] { "_legacyNrCheck", "_legacyNotchCheck" })
            {
                foreach (string handler in new[] { ".Checked +=", ".Unchecked +=" })
                {
                    int at = panel.IndexOf(control + handler, StringComparison.Ordinal);
                    Assert.True(at >= 0, control + handler + " not found");
                    int end = panel.IndexOf(");", at, StringComparison.Ordinal);
                    Assert.DoesNotContain("ToggleGatedRig", panel.Substring(at, end - at));
                }
            }
        }

        /// <summary>
        /// Every feature Feature Availability reports as unlicensed has a gated
        /// write, and that gate is closed on the same radio.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the guard that would have caught #746, and the item-level
        /// guard above would not.</b> <c>noise_reduction</c> was already read
        /// by three rows before ANFT was gated, so "every item has a row"
        /// passed while a switch on that item had none. The defect was two
        /// surfaces disagreeing about one radio: Feature Availability said
        /// unsubscribed, the key said on. So the test reads the surface that
        /// already knows, and demands the write gate agree.
        /// </para>
        /// <para>
        /// Agreement runs both ways. The repair round of 2026-10-27 found that
        /// Feature Availability was itself wrong about NRL and ANFL (the radio
        /// runs them unlicensed), so they left BOTH surfaces at once: this
        /// list, and the gate. A NEW unsubscribed line with no entry in the map
        /// fails here as unmapped, which is the point: a feature reported as
        /// licensed is a switch somebody must decide how to gate, against the
        /// radio's own answer.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryFeatureAvailabilityCallsUnsubscribedIsAClosedWriteGate()
        {
            var lineToCapability = new Dictionary<string, RadioCapability>(StringComparer.Ordinal)
            {
                ["ESC"] = RadioCapability.Esc,
                ["Noise Reduction (RNN)"] = RadioCapability.NoiseReductionNeural,
                ["Noise Reduction (NRF)"] = RadioCapability.NoiseReductionFilter,
                ["Noise Reduction (NRS)"] = RadioCapability.NoiseReductionSpectral,
                ["Auto Notch (ANFT)"] = RadioCapability.AutoNotchFft,
            };

            var divEsc = new Feature("div_esc", false, FeatureStatusReason.Plus);
            FlexBase rig = OfflineDiversityRig(sliceCount: 1, divEsc);
            try
            {
                rig.theRadio!.FeatureLicense.LicenseFeatNoiseReduction =
                    new Feature("noise_reduction", false, FeatureStatusReason.Plus);

                var unsubscribed = rig.BuildFeatureAvailabilityText()
                    .Split(new[] { Environment.NewLine }, StringSplitOptions.None)
                    .Where(l => l.Contains(": unsubscribed", StringComparison.Ordinal))
                    .Select(l => l.Substring(0, l.IndexOf(':')))
                    .ToList();

                // Positive control: the surface really does report the licence
                // state this radio was given, so an empty list cannot pass.
                Assert.Equal(5, unsubscribed.Count);

                foreach (string feature in unsubscribed)
                {
                    Assert.True(lineToCapability.TryGetValue(feature, out var capability),
                        "Feature Availability calls '" + feature + "' unsubscribed and no gated capability maps to it");
                    Assert.True(rig.CapabilityIsClosedOnThisRadio(capability),
                        "Feature Availability calls '" + feature + "' unsubscribed but " + capability
                        + "'s write gate is open on the same radio");
                }
            }
            finally
            {
                DisposeOfflineRig(rig);
            }
        }

        /// <summary>
        /// FFT auto-notch runs on every model, so no model may be refused it on
        /// hardware - that would turn a licence question into "your radio
        /// cannot", which is false on a 6300.
        /// </summary>
        [Fact]
        public void FftAutoNotchHasNoHardwareRung()
        {
            var descriptor = FlexBase.DescriptorFor(RadioCapability.AutoNotchFft);
            Assert.Null(descriptor.Hardware);
            Assert.NotNull(descriptor.Licence);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.LicenceDisabledFallbackText));
        }

        /// <summary>
        /// Every licensed row has one owned, reason-aware seam ready for Noel's
        /// reviewed #720 wording; adding a new licensed capability without the
        /// plumbing must fail before it can fall straight through to FlexLib.
        /// </summary>
        [Fact]
        public void EveryLicensedCapabilityHasAReasonAwareOwnedWordingSeam()
        {
            foreach (RadioCapability capability in FlexBase.GatedCapabilities)
            {
                var descriptor = FlexBase.DescriptorFor(capability);
                if (descriptor.Licence == null) continue;

                Assert.NotNull(descriptor.TierAwareLicenceDisabledText);
                foreach (FeatureStatusReason reason in Enum.GetValues<FeatureStatusReason>())
                {
                    // The placeholders deliberately return null until Noel
                    // supplies the words. Invoking every branch is the test:
                    // the reason vocabulary is wired now, not at prose time.
                    descriptor.TierAwareLicenceDisabledText!(reason, FlexBase.CapabilityWording.Short);
                    descriptor.TierAwareLicenceDisabledText!(reason, FlexBase.CapabilityWording.Long);
                }
            }
        }

        /// <summary>
        /// Immediate diversity feedback must use the target captured before
        /// the queued radio write, never the stale property read-back.
        /// </summary>
        [Fact]
        public void DiversityFeedbackUsesTheQueuedTargetNotTheStateItJustLeft()
        {
            // Every FlexBase partial, each parsed on its own (a #define is
            // file-scoped, so glued files would parse differently).
            var toggle = FlexBasePartials.Files()
                .SelectMany(f => CSharpSyntaxTree.ParseText(f.Text).GetRoot()
                    .DescendantNodes().OfType<MethodDeclarationSyntax>())
                .Single(m => m.Identifier.ValueText == "ToggleDiversity");

            Assert.Equal("bool", toggle.ReturnType.ToString());
            Assert.Contains(toggle.DescendantNodes().OfType<ReturnStatementSyntax>(),
                statement => statement.Expression?.ToString() == "target");

            string menu = ReadRepoFile("JJFlexWpf/NativeMenuBar.cs");
            Assert.Contains("bool target = Rig.ToggleDiversity();", menu, StringComparison.Ordinal);
            Assert.Contains("EarconPlayer.ToggleTone(target);", menu, StringComparison.Ordinal);
            Assert.DoesNotContain("EarconPlayer.ToggleTone(Rig.DiversityOn);", menu, StringComparison.Ordinal);
            Assert.DoesNotContain("SpeakAfterMenuClose(Rig.DiversityOn", menu, StringComparison.Ordinal);
        }

        /// <summary>
        /// The hardware matrix, both directions, by model name.
        /// </summary>
        /// <remarks>
        /// FlexRadio's SmartSDR v4.1.3 release notes: NRF, NRS and RNN are
        /// "Only available on 8000 Series Radios", marked footnote 1 (Aurora,
        /// FLEX-8000). NRL, ANFL and ANFT carry footnote 1,2 and also run on the
        /// 6000s, which is why they are deliberately not gated on this.
        /// <b>The AU-510 is the row that matters</b>: it is a single-SCU Aurora,
        /// and an earlier predicate excluded it by confusing RNN with Enhanced
        /// Signal Clarity, which really is dual-SCU only (#729).
        /// </remarks>
        [Theory]
        // Supported: the whole 8000 series and the whole Aurora family.
        [InlineData("FLEX-8400", true)]
        [InlineData("FLEX-8400M", true)]
        [InlineData("FLEX-8600", true)]
        [InlineData("FLEX-8600M", true)]
        [InlineData("AU-510", true)]
        [InlineData("AU-510M", true)]
        [InlineData("AU-520", true)]
        [InlineData("AU-520M", true)]
        // Not supported: every 6000-series radio lacks the DSP hardware, and a
        // SmartSDR+ licence does not change that.
        [InlineData("FLEX-6300", false)]
        [InlineData("FLEX-6400", false)]
        [InlineData("FLEX-6400M", false)]
        [InlineData("FLEX-6500", false)]
        [InlineData("FLEX-6600", false)]
        [InlineData("FLEX-6600M", false)]
        [InlineData("FLEX-6700", false)]
        [InlineData("FLEX-6700R", false)]
        public void TheAdvancedNrHardwareMatrixMatchesTheVendorDocumentation(string model, bool expected)
        {
            Assert.Equal(expected, CapabilityHardware.SupportsAdvancedNoiseReduction(model));
        }

        /// <summary>
        /// A predicate that answered "no" to everything would pass a
        /// refusals-only test, so the allowed side is asserted as a set in its
        /// own right. This is the positive control for the matrix above.
        /// </summary>
        [Fact]
        public void TheMatrixSaysYesAndNoRatherThanRefusingEverything()
        {
            string[] supported = { "FLEX-8400", "FLEX-8600", "AU-510", "AU-520" };
            string[] unsupported = { "FLEX-6300", "FLEX-6500", "FLEX-6700" };

            Assert.All(supported, m => Assert.True(
                CapabilityHardware.SupportsAdvancedNoiseReduction(m), m + " should be supported"));
            Assert.All(unsupported, m => Assert.False(
                CapabilityHardware.SupportsAdvancedNoiseReduction(m), m + " should not be supported"));
        }

        /// <summary>
        /// An unknown or absent model must not be read as capable. Guessing
        /// "yes" here is the failure that produces #725's silent false "on".
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("TS-590G")]
        [InlineData("FLEX-")]
        [InlineData("AU-")]
        public void AnUnknownModelIsNotTreatedAsCapable(string model)
        {
            Assert.False(CapabilityHardware.SupportsAdvancedNoiseReduction(model));
        }

        /// <summary>
        /// Case must not decide a capability. The model string arrives from the
        /// radio and nothing guarantees its casing.
        /// </summary>
        [Theory]
        [InlineData("flex-8600", true)]
        [InlineData("au-510", true)]
        [InlineData("flex-6300", false)]
        public void TheMatrixDoesNotDependOnCase(string model, bool expected)
        {
            Assert.Equal(expected, CapabilityHardware.SupportsAdvancedNoiseReduction(model));
        }

        /// <summary>
        /// The verdict type's classification contract, which the ordering rule
        /// is expressed in terms of.
        /// </summary>
        /// <remarks>
        /// <b>The assertion that matters is the hardware one.</b> A radio told
        /// "your model cannot do this" must never also be told about a
        /// subscription, because inviting somebody to buy a licence that would
        /// do nothing for them is the actively harmful error the ladder order
        /// exists to prevent.
        /// </remarks>
        [Fact]
        public void AHardwareVerdictIsNeverAboutALicence()
        {
            var hardware = new CapabilityVerdict(
                RadioCapability.NoiseReductionNeural, CapabilityBlock.Hardware, "model lacks the DSP");
            Assert.False(hardware.Allowed);
            Assert.False(hardware.IsAboutLicence);
            Assert.False(hardware.IsOperatorFixable);

            var disabled = new CapabilityVerdict(
                RadioCapability.NoiseReductionNeural, CapabilityBlock.LicenceDisabled, "not in your subscription");
            Assert.True(disabled.IsAboutLicence);

            var notReported = new CapabilityVerdict(
                RadioCapability.NoiseReductionNeural, CapabilityBlock.LicenceNotReported, "");
            Assert.True(notReported.IsAboutLicence);

            var resource = new CapabilityVerdict(
                RadioCapability.DiversityReception, CapabilityBlock.Resource, "Need two RX antennas");
            Assert.True(resource.IsOperatorFixable);
            Assert.False(resource.IsAboutLicence);

            var available = new CapabilityVerdict(
                RadioCapability.CwAutotune, CapabilityBlock.None, "");
            Assert.True(available.Allowed);
            Assert.Equal(string.Empty, available.Message);
        }

        /// <summary>
        /// The ladder's first rung, exercised on a real <see cref="FlexBase"/>
        /// with no radio attached — which also proves every row in the table
        /// carries its not-connected wording rather than returning an empty
        /// sentence that would announce as silence.
        /// </summary>
        [Fact]
        public void WithNoRadioEveryCapabilityReportsNotConnectedAndSaysSomething()
        {
            FlexBase rig = null;
            try
            {
                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

                foreach (RadioCapability capability in Enum.GetValues<RadioCapability>())
                {
                    var verdict = rig.EvaluateCapability(capability);

                    Assert.Equal(CapabilityBlock.NotConnected, verdict.Block);
                    Assert.False(verdict.Allowed);
                    Assert.False(string.IsNullOrWhiteSpace(verdict.Message));
                }
            }
            finally
            {
                rig?.Dispose();
            }
        }

        /// <summary>
        /// A write with no radio is dropped, and <b>silently</b> — it raises no
        /// refusal.
        /// </summary>
        /// <remarks>
        /// "The radio is not here yet" is not a statement about the capability.
        /// A panel that writes a switch while building or tearing itself down
        /// would otherwise make the application announce "No radio connected" at
        /// the operator for a write they never made, and spurious speech is its
        /// own defect class. Only hardware, a positively-disabled licence, a
        /// resource or the wrong mode raise the event.
        /// </remarks>
        [Fact]
        public void ATransientBlockDropsTheWriteWithoutAnnouncingIt()
        {
            FlexBase rig = null;
            try
            {
                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

                int refusals = 0;
                rig.CapabilityWriteRefused += (_, _) => refusals++;

                // Every road through a gated switch, with no radio attached.
                rig.NeuralNoiseReduction = FlexBase.OffOnValues.on;
                rig.SpectralNoiseReduction = FlexBase.OffOnValues.on;
                rig.NoiseReductionFilter = FlexBase.OffOnValues.on;
                rig.CWAutotune();

                Assert.Equal(0, refusals);

                // And the writes genuinely did not land anywhere.
                Assert.Equal(FlexBase.OffOnValues.off, rig.NeuralNoiseReduction);
                Assert.Equal(FlexBase.OffOnValues.off, rig.SpectralNoiseReduction);
                Assert.Equal(FlexBase.OffOnValues.off, rig.NoiseReductionFilter);
            }
            finally
            {
                rig?.Dispose();
            }
        }

        // ---------------------------------------------------------------
        // The ladder itself. #726 item 8.
        //
        // These are the four tests the brief asked for, and they are possible
        // because FlexBase.DecideBlock is pure: every input is an argument, so
        // a licence state that would otherwise need a radio is one enum value.
        // ---------------------------------------------------------------

        /// <summary>A capable, licensed, well-resourced radio is ALLOWED.</summary>
        /// <remarks>
        /// <b>The positive control, and it goes first because without it the
        /// other three prove nothing.</b> A ladder that refused everything
        /// would pass every refusal test in this file. Something has to show it
        /// says yes.
        /// </remarks>
        [Fact]
        public void ACapableLicensedRadioIsAllowedThrough()
        {
            Assert.Equal(CapabilityBlock.None, FlexBase.DecideBlock(
                connected: true, requiresSlice: true, sliceSelected: true,
                hasHardwareRequirement: true, hardwareSupported: true,
                licence: FlexBase.LicenceState.Enabled,
                resourcesSatisfied: true, modeSatisfied: true));

            // And a capability with no licence requirement at all.
            Assert.Equal(CapabilityBlock.None, FlexBase.DecideBlock(
                connected: true, requiresSlice: true, sliceSelected: true,
                hasHardwareRequirement: false, hardwareSupported: false,
                licence: FlexBase.LicenceState.NotApplicable,
                resourcesSatisfied: true, modeSatisfied: true));
        }

        /// <summary>
        /// #725's shape as a regression: reported AND disabled must refuse.
        /// </summary>
        /// <remarks>
        /// <b>This asserts the REFUSAL, not that a method was called.</b> The
        /// measured defect was an 8600 reporting the licence present and
        /// disabled while three surfaces announced "on" — so the thing worth
        /// pinning is that this exact state produces a licence refusal and
        /// nothing else.
        /// </remarks>
        [Fact]
        public void APositivelyDisabledLicenceRefuses()
        {
            var block = FlexBase.DecideBlock(
                connected: true, requiresSlice: true, sliceSelected: true,
                hasHardwareRequirement: true, hardwareSupported: true,
                licence: FlexBase.LicenceState.Disabled,
                resourcesSatisfied: true, modeSatisfied: true);

            Assert.Equal(CapabilityBlock.LicenceDisabled, block);
            Assert.True(FlexBase.BlocksWrite(block), "a disabled licence must stop the write");
        }

        /// <summary>
        /// The three-state asymmetry: never-reported is NOT disabled.
        /// </summary>
        /// <remarks>
        /// A licence we were never told about is not evidence of anything, so
        /// it reports its own block AND must not stop a write. If these two
        /// ever collapse into one state, an application that has simply not
        /// heard from the radio yet starts removing features the operator owns.
        /// </remarks>
        [Fact]
        public void NeverReportedBehavesDifferentlyFromPositivelyDisabled()
        {
            var notReported = FlexBase.DecideBlock(
                connected: true, requiresSlice: true, sliceSelected: true,
                hasHardwareRequirement: true, hardwareSupported: true,
                licence: FlexBase.LicenceState.NotReported,
                resourcesSatisfied: true, modeSatisfied: true);

            var disabled = FlexBase.DecideBlock(
                connected: true, requiresSlice: true, sliceSelected: true,
                hasHardwareRequirement: true, hardwareSupported: true,
                licence: FlexBase.LicenceState.Disabled,
                resourcesSatisfied: true, modeSatisfied: true);

            Assert.NotEqual(disabled, notReported);
            Assert.Equal(CapabilityBlock.LicenceNotReported, notReported);

            // The load-bearing half: silence does not refuse.
            Assert.False(FlexBase.BlocksWrite(notReported),
                "a licence we were never told about must not stop a write");
            Assert.True(FlexBase.BlocksWrite(disabled));
        }

        /// <summary>
        /// Hardware is decided before any licence, so a radio that cannot run
        /// the feature is never told about a subscription.
        /// </summary>
        /// <remarks>
        /// <b>The harmful-error test, and the one most worth having.</b>
        /// Inviting somebody to buy a licence for hardware they do not have
        /// costs them money for nothing. Asserted against every licence state,
        /// including Enabled — a 6300 owner who somehow holds the licence still
        /// has to be told it is the model, not the subscription.
        /// </remarks>
        [Theory]
        [InlineData(FlexBase.LicenceState.NotReported)]
        [InlineData(FlexBase.LicenceState.Disabled)]
        [InlineData(FlexBase.LicenceState.Enabled)]
        [InlineData(FlexBase.LicenceState.NotApplicable)]
        public void MissingHardwareIsNeverReportedAsALicenceProblem(FlexBase.LicenceState licence)
        {
            var block = FlexBase.DecideBlock(
                connected: true, requiresSlice: true, sliceSelected: true,
                hasHardwareRequirement: true, hardwareSupported: false,
                licence: licence,
                resourcesSatisfied: false, modeSatisfied: false);

            Assert.Equal(CapabilityBlock.Hardware, block);

            // Said the other way round, because this is the sentence that
            // matters to an operator: the verdict is not about a licence.
            var verdict = new CapabilityVerdict(RadioCapability.NoiseReductionNeural, block, "x");
            Assert.False(verdict.IsAboutLicence);
        }

        /// <summary>
        /// Operator-fixable reasons come last, so a radio without the licence
        /// hears about the licence rather than about antennas.
        /// </summary>
        [Fact]
        public void OperatorFixableReasonsComeAfterTheLicence()
        {
            // Licence disabled AND resources missing AND wrong mode: the
            // licence is the answer.
            Assert.Equal(CapabilityBlock.LicenceDisabled, FlexBase.DecideBlock(
                connected: true, requiresSlice: true, sliceSelected: true,
                hasHardwareRequirement: true, hardwareSupported: true,
                licence: FlexBase.LicenceState.Disabled,
                resourcesSatisfied: false, modeSatisfied: false));

            // Everything allowed but a resource missing: resources, not mode.
            Assert.Equal(CapabilityBlock.Resource, FlexBase.DecideBlock(
                connected: true, requiresSlice: true, sliceSelected: true,
                hasHardwareRequirement: true, hardwareSupported: true,
                licence: FlexBase.LicenceState.Enabled,
                resourcesSatisfied: false, modeSatisfied: false));

            // Only the mode left.
            Assert.Equal(CapabilityBlock.Mode, FlexBase.DecideBlock(
                connected: true, requiresSlice: true, sliceSelected: true,
                hasHardwareRequirement: true, hardwareSupported: true,
                licence: FlexBase.LicenceState.Enabled,
                resourcesSatisfied: true, modeSatisfied: false));
        }

        /// <summary>
        /// The transient rungs come first and say nothing about the capability.
        /// </summary>
        [Fact]
        public void NotConnectedAndNoSliceOutrankEveryCapabilityReason()
        {
            // Not connected wins over everything, including missing hardware.
            Assert.Equal(CapabilityBlock.NotConnected, FlexBase.DecideBlock(
                connected: false, requiresSlice: true, sliceSelected: false,
                hasHardwareRequirement: true, hardwareSupported: false,
                licence: FlexBase.LicenceState.Disabled,
                resourcesSatisfied: false, modeSatisfied: false));

            // No slice wins over hardware too: it is transient and the
            // hardware answer will still be there once a slice is selected.
            Assert.Equal(CapabilityBlock.NoSlice, FlexBase.DecideBlock(
                connected: true, requiresSlice: true, sliceSelected: false,
                hasHardwareRequirement: true, hardwareSupported: false,
                licence: FlexBase.LicenceState.Disabled,
                resourcesSatisfied: false, modeSatisfied: false));

            // A capability needing no slice skips that rung.
            Assert.Equal(CapabilityBlock.Hardware, FlexBase.DecideBlock(
                connected: true, requiresSlice: false, sliceSelected: false,
                hasHardwareRequirement: true, hardwareSupported: false,
                licence: FlexBase.LicenceState.Enabled,
                resourcesSatisfied: true, modeSatisfied: true));

            // And neither transient rung stops a write by itself - they drop it
            // silently instead, which is what keeps a panel building itself
            // from announcing "No radio connected" at the operator.
            Assert.False(FlexBase.BlocksWrite(CapabilityBlock.NotConnected));
            Assert.False(FlexBase.BlocksWrite(CapabilityBlock.NoSlice));
        }

        /// <summary>
        /// Mode is reported but does not stop a write, pending one bench
        /// reading.
        /// </summary>
        /// <remarks>
        /// <b>This test pins a deliberate gap, and says so.</b> The belief that
        /// advanced NR is unavailable in CW and FM comes from one status builder
        /// in this codebase and has never been checked against a radio. Until it
        /// is, the ladder reports mode so a readout can explain itself and the
        /// write goes through anyway — because refusing on an unsourced belief
        /// would remove a working feature across a whole class of modes, which
        /// is the defect this class exists to prevent.
        /// <para>
        /// <b>When the bench reading says mode really does block, change this
        /// test deliberately</b> rather than deleting it: it is the record of
        /// why the gap was left.
        /// </para>
        /// </remarks>
        [Fact]
        public void ModeIsReportedButAdvisoryUntilItHasBeenMeasured()
        {
            Assert.Equal(CapabilityBlock.Mode, FlexBase.DecideBlock(
                connected: true, requiresSlice: true, sliceSelected: true,
                hasHardwareRequirement: true, hardwareSupported: true,
                licence: FlexBase.LicenceState.Enabled,
                resourcesSatisfied: true, modeSatisfied: false));

            Assert.False(FlexBase.BlocksWrite(CapabilityBlock.Mode),
                "mode is advisory until somebody turns NR on in CW on a real radio");
        }

        /// <summary>
        /// Every road to the three gated switches asks the ladder before it
        /// speaks.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What this catches and what it does not.</b> It reads the three
        /// known roads' source and asserts each write is accompanied by its
        /// capability. It cannot catch a BRAND NEW road that writes the
        /// properties some other way — nothing short of a real UI walk can.
        /// What that fourth road would get is a refused write, because the
        /// setter is gated, plus a <c>CapabilityWriteRefused</c> trace line;
        /// what it would lose is only the spoken reason.
        /// </para>
        /// <para>
        /// It is worth having anyway because the failure it guards against is
        /// the one that actually happened three times: a road that writes and
        /// announces without asking (#725). A source assertion is the only kind
        /// available here — these roads are WPF surfaces, and the test project
        /// that can construct them refuses to run on a live desk.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs", "_neuralNrCheck", "NoiseReductionNeural")]
        [InlineData("JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs", "_spectralNrCheck", "NoiseReductionSpectral")]
        [InlineData("JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs", "_nrfCheck", "NoiseReductionFilter")]
        [InlineData("JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs", "_fftNotchCheck", "AutoNotchFft")]
        public void TheExpandersCheckboxesAskTheLadderBeforeTheySpeak(
            string relativePath, string control, string capability)
        {
            string source = ReadRepoFile(relativePath);

            // Both directions of the checkbox, and both must be gated: an
            // operator turning a refused switch OFF is just as entitled to hear
            // why nothing happened as one turning it on.
            foreach (string handler in new[] { ".Checked +=", ".Unchecked +=" })
            {
                int at = source.IndexOf(control + handler, StringComparison.Ordinal);
                Assert.True(at >= 0, control + handler + " not found in " + relativePath);

                // The handler is one statement; read to its terminator.
                int end = source.IndexOf(");", at, StringComparison.Ordinal);
                Assert.True(end > at, "unterminated handler for " + control + handler);
                string statement = source.Substring(at, end - at);

                Assert.Contains("ToggleGatedRig", statement);
                Assert.Contains("RadioCapability." + capability, statement);
            }
        }

        /// <summary>
        /// The Radio menu's licensed noise items pass their capability to
        /// <c>ToggleDSP</c>, and the switches that need no gate do not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The negative half is the half worth having: a gate applied to
        /// things that do not need one is how a gate stops meaning anything and
        /// starts getting removed wholesale.
        /// </para>
        /// <para>
        /// <b>A record of a correction that was itself wrong.</b> Before #746
        /// this test held Legacy NR ungated because it "runs on every model and
        /// needs no licence". The first #746 build called that a pinned bug,
        /// trusting FlexRadio's documents, and gated it. The radio then
        /// settled it on 2026-10-27: an unlicensed 8600 accepted lms_nr and
        /// lms_anf and refused only anft. So the original assertion was right,
        /// Legacy NR and Legacy Auto-Notch are back on the ungated side beside
        /// the blankers, and FFT Auto-Notch joins the gated side.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheRadioMenuGatesExactlyTheSwitchesThatNeedIt()
        {
            string source = ReadRepoFile("JJFlexWpf/NativeMenuBar.cs");

            foreach (var (label, capability) in new[]
                     {
                         ("On-Radio Neural NR", "NoiseReductionNeural"),
                         ("On-Radio Spectral NR", "NoiseReductionSpectral"),
                         ("NR Filter", "NoiseReductionFilter"),
                         ("FFT Auto-Notch", "AutoNotchFft"),
                     })
            {
                int at = source.IndexOf("ToggleDSP(\"" + label + "\"", StringComparison.Ordinal);
                Assert.True(at >= 0, "no ToggleDSP call for " + label);
                int end = source.IndexOf(");", at, StringComparison.Ordinal);
                Assert.True(end > at, "unterminated ToggleDSP call for " + label);

                Assert.Contains("RadioCapability." + capability, source.Substring(at, end - at));
            }

            // The noise blankers, Legacy NR and Legacy Auto-Notch run on every
            // model with no licence (the last two measured, #746), and are
            // deliberately ungated.
            foreach (string ungated in new[] { "Noise Blanker", "Wideband NB", "Legacy NR", "Legacy Auto-Notch" })
            {
                int at = source.IndexOf("ToggleDSP(\"" + ungated + "\"", StringComparison.Ordinal);
                Assert.True(at >= 0, "no ToggleDSP call for " + ungated);
                int end = source.IndexOf(");", at, StringComparison.Ordinal);
                Assert.DoesNotContain("RadioCapability.", source.Substring(at, end - at));
            }
        }

        /// <summary>
        /// The noise layer's A key asks the ladder before it speaks, so a
        /// refused auto notch is announced as refused rather than as "on".
        /// </summary>
        /// <remarks>
        /// #746's entry said A would inherit the gate "with no change to
        /// KeyCommands.cs". It inherits the refused WRITE that way, because
        /// the setter is gated - but not the SPEECH: a RadioSwitch with no
        /// gate says "{label} on" whatever the setter did. Source-level, like
        /// the two road tests above, because the layer lives in a WPF project.
        /// </remarks>
        [Fact]
        public void TheNoiseLayersAutoNotchKeyAsksTheLadderBeforeItSpeaks()
        {
            string source = ReadRepoFile("JJFlexWpf/KeyCommands.cs");
            int at = source.IndexOf("RadioSwitch(\"auto-notch\"", StringComparison.Ordinal);
            Assert.True(at >= 0, "no RadioSwitch for auto-notch in KeyCommands.cs");
            int end = source.IndexOf(");", at, StringComparison.Ordinal);
            Assert.True(end > at, "unterminated RadioSwitch for auto-notch");

            Assert.Contains("CapabilityWriteRefusal(Radios.RadioCapability.AutoNotchFft)",
                source.Substring(at, end - at));
        }

        /// <summary>
        /// Find a file by walking up from the test binary to the repository
        /// root, the way the integration-pass scans do.
        /// </summary>
        private static string ReadRepoFile(string relativePath)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            string full = System.IO.Path.Combine(dir!.FullName, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Assert.True(System.IO.File.Exists(full), "not found: " + full);
            return System.IO.File.ReadAllText(full);
        }

        private static FlexBase OfflineDiversityRig(int sliceCount, Feature? divEsc = null)
        {
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            var radio = (Radio)Activator.CreateInstance(
                typeof(Radio), Private, binder: null, args: new object[] { false }, culture: null)!;

            typeof(Radio).GetField("_model", Private)!.SetValue(radio, "FLEX-8600");
            typeof(Radio).GetField("_rx_ant_list", Private)!
                .SetValue(radio, new[] { "ANT1", "ANT2" });
            radio.FeatureLicense.LicenseFeatDivEsc = divEsc;

            for (int index = 0; index < sliceCount; index++)
            {
                var slice = (Slice)Activator.CreateInstance(
                    typeof(Slice), Private, binder: null, args: new object[] { radio }, culture: null)!;
                typeof(Slice).GetField("_index", Private)!.SetValue(slice, index);
                typeof(Slice).GetField("_active", Private)!.SetValue(slice, index == 0);
                radio.SliceList.Add(slice);
            }

            rig.theRadio = radio;
            return rig;
        }

        private static void DisposeOfflineRig(FlexBase rig)
        {
            // Do not let disposal try to disconnect the offline vendor object.
            rig.theRadio = null;
            rig.Dispose();
        }

        /// <summary>
        /// <see cref="FlexBase.CapabilityRefusal"/> returns null for available
        /// and for a licence we were never told about, because not knowing is
        /// not a reason to refuse.
        /// </summary>
        [Fact]
        public void NotKnowingAboutALicenceIsNotARefusal()
        {
            var notReported = new CapabilityVerdict(
                RadioCapability.NoiseReductionNeural, CapabilityBlock.LicenceNotReported, "");

            // The verdict still CLASSIFIES it - a status readout may want to say
            // "pending" - while the refusal path declines to act on it. Both
            // behaviours come from the one ladder, which is the point.
            Assert.Equal(CapabilityBlock.LicenceNotReported, notReported.Block);
            Assert.False(notReported.Allowed);
            Assert.True(notReported.IsAboutLicence);
        }
    }
}
