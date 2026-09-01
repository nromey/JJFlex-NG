using System;
using System.IO;
using System.Linq;
using Radios.ChainChecks;
using Radios.Fixer;
using Radios.Fixer.Evidence;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// ONE READING, ONE UNIT WORD (#444).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FlexBase.XmitPower</c> is read once and used three times, and until 1
    /// September 2026 one Fixer report could say all of this about that single
    /// number: <i>"at 10 watts into ANT1"</i> in the stage sentence, <i>"RF
    /// power: 10 watts"</i> in the settings fingerprint, and <i>"Transmit power
    /// setting: 10 percent"</i> in the readings block. On a hundred-watt radio
    /// the digits agree, so nothing looked wrong and nothing ever would — until
    /// an amplifier, a transverter, or a radio whose maximum is not a hundred.
    /// </para>
    /// <para>
    /// <b>These tests do not claim watts is the correct unit.</b> That question
    /// is open, it covers <c>RFPower</c>, <c>TunePower</c> and
    /// <c>AMCarrierLevel</c> together (#426), and it is answered on a bench with
    /// a wattmeter rather than from a vendor comment. What is pinned here is
    /// that the app answers it ONCE — see <see cref="TxPowerPhrasing"/> — so
    /// that when the bench does answer, one edit moves every surface.
    /// </para>
    /// <para>
    /// Same discipline as <c>ReflectedThresholdAgreementTests</c>, and for the
    /// same reason: a comment asking future editors to keep figures in step has
    /// already been ignored once in this codebase.
    /// </para>
    /// </remarks>
    public sealed class TransmitPowerUnitAgreementTests
    {
        [Fact]
        public void The_stage_sentence_and_the_fingerprint_say_the_same_words_for_one_number()
        {
            // Driven through the real APIs, not through the constant, so this
            // fails if either surface grows a literal of its own again.
            var set = TransmitStageSet.Build(new TransmitStageSet.Hosts
            {
                ReadStation = () => new TransmitStageSet.StationNow
                {
                    RfPowerWatts = 7,
                    TunePowerWatts = 7,
                    AntennaPort = "ANT1",
                },
            });

            string sentence = set.Find(TransmitStageSet.SpokenTransmit).DescribeRunAction();

            FixerSettingProbeSet probes = TransmitSettingProbes.Build(new TransmitSettingReaders
            {
                RfPowerWatts = () => 7,
                TunePowerWatts = () => 7,
            });
            string fingerprint = probes.CaptureFor(TransmitStageSet.SpokenTransmit)
                .Single(p => p.Key == TransmitSettingProbes.RfPower).Value;

            Assert.Equal(TxPowerPhrasing.Setting(7), fingerprint);
            Assert.Contains(TxPowerPhrasing.Setting(7), sentence);
        }

        [Fact]
        public void The_readings_block_takes_its_unit_from_the_same_place()
        {
            // TxChainFacts needs a live radio, so this is the one surface that
            // has to be read from source. It is the surface that was WRONG, so
            // leaving it unchecked would leave the defect free to come back.
            string source = File.ReadAllText(Path.Combine(
                RepoRoot(), "Radios", "ChainChecks", "TxChainFacts.cs"));

            int at = source.IndexOf("\"rf-power-setting\"", StringComparison.Ordinal);
            Assert.True(at >= 0, "TxChainFacts no longer collects rf-power-setting");

            // The probe body, up to the end of that statement.
            int end = source.IndexOf("));", at, StringComparison.Ordinal);
            Assert.True(end > at, "could not find the end of the rf-power-setting probe");
            string probe = source.Substring(at, end - at);

            Assert.Contains("TxPowerPhrasing.SettingUnits", probe);
            Assert.DoesNotContain("\"percent\"", probe);
            Assert.DoesNotContain("\"watts\"", probe);
        }

        [Fact]
        public void One_is_singular_because_these_sentences_are_read_aloud()
        {
            Assert.Equal("1 watt", TxPowerPhrasing.Setting(1));
            Assert.Equal("0 " + TxPowerPhrasing.SettingUnits, TxPowerPhrasing.Setting(0));
            Assert.Equal("100 " + TxPowerPhrasing.SettingUnits, TxPowerPhrasing.Setting(100));
        }

        [Fact]
        public void No_other_transmit_power_surface_carries_a_unit_word_of_its_own()
        {
            // The scan that notices a FOURTH consumer. It is deliberately narrow
            // — three files, the ones that render this one reading — because a
            // whole-tree ban on the word "watts" would fire on the Power dialog,
            // the profile report and the rules file, all of which are talking
            // about other things.
            string[] files =
            {
                Path.Combine("Radios", "Fixer", "TransmitStageSet.cs"),
                Path.Combine("Radios", "FixerEvidence", "FixerSettingsFingerprint.cs"),
            };

            foreach (string relative in files)
            {
                string source = File.ReadAllText(Path.Combine(RepoRoot(), relative));

                // Comments explaining the history are allowed to use the words;
                // code is not.
                foreach (string line in source.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (t.StartsWith("///", StringComparison.Ordinal)) continue;
                    if (t.StartsWith("*", StringComparison.Ordinal)) continue;

                    Assert.False(t.Contains("\" watts\"") || t.Contains("\" watt\"")
                                 || t.Contains("\"watts\"") || t.Contains("\"percent\""),
                        relative + " puts a transmit power unit of its own in code: " + t
                        + " — call TxPowerPhrasing instead (#444).");
                }
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
