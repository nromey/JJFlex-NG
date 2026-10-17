using System;
using System.Collections.Generic;
using System.Linq;
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
        /// The Radio menu's three advanced-NR items pass their capability to
        /// <c>ToggleDSP</c>, and the switches that need no gate do not.
        /// </summary>
        /// <remarks>
        /// The negative half is the half worth having. Legacy NR runs on every
        /// model and needs no licence, so gating it would be wrong — and a gate
        /// applied to things that do not need one is how a gate stops meaning
        /// anything and starts getting removed wholesale.
        /// </remarks>
        [Fact]
        public void TheRadioMenuGatesExactlyTheThreeThatNeedIt()
        {
            string source = ReadRepoFile("JJFlexWpf/NativeMenuBar.cs");

            foreach (var (label, capability) in new[]
                     {
                         ("On-Radio Neural NR", "NoiseReductionNeural"),
                         ("On-Radio Spectral NR", "NoiseReductionSpectral"),
                         ("NR Filter", "NoiseReductionFilter"),
                     })
            {
                int at = source.IndexOf("ToggleDSP(\"" + label + "\"", StringComparison.Ordinal);
                Assert.True(at >= 0, "no ToggleDSP call for " + label);
                int end = source.IndexOf(");", at, StringComparison.Ordinal);
                Assert.True(end > at, "unterminated ToggleDSP call for " + label);

                Assert.Contains("RadioCapability." + capability, source.Substring(at, end - at));
            }

            // Legacy NR is deliberately ungated.
            int legacy = source.IndexOf("ToggleDSP(\"Legacy NR\"", StringComparison.Ordinal);
            Assert.True(legacy >= 0, "no ToggleDSP call for Legacy NR");
            int legacyEnd = source.IndexOf(");", legacy, StringComparison.Ordinal);
            Assert.DoesNotContain("RadioCapability.", source.Substring(legacy, legacyEnd - legacy));
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
