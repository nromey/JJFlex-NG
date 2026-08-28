using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The kill switch's state machine, without a radio.
    /// </summary>
    /// <remarks>
    /// Everything that needs a <c>FlexBase</c> is untestable here by
    /// construction, so these cover the half that decides: arming, latching,
    /// and the refusal that stands between a keying site and an unstoppable
    /// carrier. The routing itself is checked by reading source, below.
    /// </remarks>
    public sealed class TransmitKillSwitchTests : IDisposable
    {
        public TransmitKillSwitchTests() => TransmitKillSwitch.ResetForTests();

        public void Dispose() => TransmitKillSwitch.ResetForTests();

        [Fact]
        public void NothingIsArmedToStartWith()
        {
            Assert.False(TransmitKillSwitch.Armed);
            Assert.False(TransmitKillSwitch.KillRequested);
            Assert.Equal(0, TransmitKillSwitch.KillCount);
        }

        [Fact]
        public void RaisingACarrierWithNothingArmedIsRefused()
        {
            // The whole point. A keying site that lost its arm must not
            // transmit at all — a refusal costs a measurement, and the
            // alternative costs a carrier nobody can drop.
            Assert.False(TransmitKillSwitch.RaiseCarrier(null, TransmitKillSwitch.Carrier.Mox));
            Assert.False(TransmitKillSwitch.RaiseCarrier(null, TransmitKillSwitch.Carrier.Tune));
        }

        [Fact]
        public void ARequestWithNothingArmedSaysNothingAndCountsNothing()
        {
            // Silence is correct here. "Transmit stopped" when nothing was
            // transmitting teaches an operator that the sentence means nothing,
            // which is the sentence they will need to trust one day.
            TransmitKillSwitch.Request(TransmitKillSwitch.Source.HostRequest);
            Assert.False(TransmitKillSwitch.KillRequested);
            Assert.Equal(0, TransmitKillSwitch.KillCount);
        }

        [Fact]
        public void DroppingACarrierNeverThrowsAndNeverNeedsAnArm()
        {
            // An unkey is the one step that must never be conditional.
            TransmitKillSwitch.DropCarrier(null, TransmitKillSwitch.Carrier.Mox);
            TransmitKillSwitch.DropCarrier(null, TransmitKillSwitch.Carrier.Tune);
        }

        [Fact]
        public void ArmingWithNoRadioArmsNothing()
        {
            using (TransmitKillSwitch.Arm(null, "a check with no radio"))
            {
                Assert.False(TransmitKillSwitch.Armed);
            }
        }
    }

    /// <summary>
    /// Every keying site in the transmit checks goes through the kill switch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This reads source, and it has to.</b> The thing being verified is
    /// that no author wrote <c>rig.Transmit = true</c> next to the machinery
    /// instead of through it — the same failure the Fixer Tool exists to
    /// expose, turned on the tool itself. A behavioural test cannot see a
    /// keying site that was added without an arm, because such a site works
    /// perfectly right up until somebody wants out.
    /// </para>
    /// <para>
    /// Shaped on <c>LexiconKeyCoverageTests</c>, including its positive
    /// control: a sweep that finds nothing must prove it looked, or a broken
    /// root-finder reads as a clean bill of health.
    /// </para>
    /// </remarks>
    public sealed class TransmitKillSwitchRoutingTests
    {
        /// <summary>
        /// An assignment to <c>Transmit</c> or <c>TxTune</c> — not a comparison
        /// against one. <c>rig.Transmit == wantKeyed</c> is a read and appears
        /// several times in the files swept.
        /// </summary>
        private static readonly Regex CarrierWrite = new Regex(
            @"\.\s*(Transmit|TxTune)\s*=(?!=)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ThroughTheSwitch = new Regex(
            @"TransmitKillSwitch\s*\.\s*(RaiseCarrier|DropCarrier)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Arms = new Regex(
            @"TransmitKillSwitch\s*\.\s*Arm\s*\(",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        [Fact]
        public void NoTransmitCheckWritesACarrierDirectly()
        {
            string dir = ChainChecksDir();
            var offenders = new List<string>();
            int scanned = 0, throughSwitch = 0, arming = 0;

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs"))
            {
                scanned++;
                string text = File.ReadAllText(file);
                if (ThroughTheSwitch.IsMatch(text)) throughSwitch++;
                if (Arms.IsMatch(text)) arming++;

                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int comment = line.IndexOf("//", StringComparison.Ordinal);
                    string code = comment >= 0 ? line.Substring(0, comment) : line;
                    if (CarrierWrite.IsMatch(code))
                        offenders.Add(Path.GetFileName(file) + ":" + (i + 1) + "  " + line.Trim());
                }
            }

            // Positive control, three ways. A sweep that reports "no direct
            // writes" is also claiming it would have SEEN one, and a wrong
            // directory or a wrong pattern makes the same claim just as
            // cheerfully.
            Assert.True(scanned > 10,
                "Only " + scanned + " files were scanned in " + dir
                + ", which means the sweep verified nothing.");
            Assert.True(throughSwitch >= 2,
                "The sweep found " + throughSwitch + " files keying through "
                + "TransmitKillSwitch. Both keying files — FixerTransmitAudioBoundary "
                + "and TxTuneProbeRunner — must appear, or the pattern is wrong and "
                + "the absence of offenders means nothing.");
            Assert.True(arming >= 2,
                "The sweep found " + arming + " files arming the kill switch; the two "
                + "keying files must both arm it.");

            Assert.True(offenders.Count == 0,
                "These transmit-check lines raise or drop a carrier without going "
                + "through TransmitKillSwitch, so the operator would have no way to "
                + "stop them while the stage blocks the UI thread (#236):"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        [Fact]
        public void TheSweepCanSeeADirectWriteWhenThereIsOne()
        {
            // The negative result above needs a positive control of its own:
            // prove the pattern matches the exact text it is looking for, and
            // does NOT match the reads that legitimately sit beside it.
            Assert.True(CarrierWrite.IsMatch("rig.Transmit = true;"));
            Assert.True(CarrierWrite.IsMatch("rig.TxTune = false;"));
            Assert.True(CarrierWrite.IsMatch("            rig . Transmit=true;"));
            Assert.False(CarrierWrite.IsMatch("if (rig.Transmit == wantKeyed) return true;"));
            Assert.False(CarrierWrite.IsMatch("return rig.Transmit || rig.TxTune;"));
        }

        [Fact]
        public void BothKeyingFilesAreStillWhereTheSweepLooks()
        {
            // Names the two files outright. If a rename moves keying out of
            // this directory, this fails and says so, rather than the sweep
            // quietly passing over a directory that no longer keys anything.
            string dir = ChainChecksDir();
            Assert.True(File.Exists(Path.Combine(dir, "FixerTransmitAudioBoundary.cs")));
            Assert.True(File.Exists(Path.Combine(dir, "TxTuneProbeRunner.cs")));
        }

        private static string ChainChecksDir()
        {
            string dir = Path.Combine(RepoRoot(), "Radios", "ChainChecks");
            Assert.True(Directory.Exists(dir),
                "The transmit-check source directory was not found at " + dir
                + ", so this check verified nothing.");
            return dir;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
