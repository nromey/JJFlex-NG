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
        public void TheHoldAfterACutRefusesTheCarrierAndLeavesItsSentenceForTheCheck()
        {
            // #571: a transmit check's carrier into the same fault is RF like
            // any other. The hook speaks for itself (it is the controller's
            // RefuseIfHeldAfterCut) and hands back the sentence; the gate
            // refuses before the carrier is recorded as raised, and the
            // check's detail line gets the same words the operator heard.
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            try
            {
                string asked = null;
                TransmitKillSwitch.TransmitHeld = door => { asked = door; return "Not keyed. Marker sentence."; };
                using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
                {
                    Assert.False(TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox));
                    Assert.Equal("a transmit check", asked);
                    Assert.Equal("Not keyed. Marker sentence.", TransmitKillSwitch.LastRaiseRefusal);

                    // Positive control, same arm: with the hold answering "not
                    // held" the same raise goes through, and the stale sentence
                    // is cleared so the check cannot report a refusal that did
                    // not happen.
                    TransmitKillSwitch.TransmitHeld = door => null;
                    Assert.True(TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox));
                    Assert.Null(TransmitKillSwitch.LastRaiseRefusal);
                }
            }
            finally { try { rig.Dispose(); } catch { } }
        }

        [Fact]
        public void AReflectedPowerCutTellsTheHoldSubscriberBeforeAnythingWaits()
        {
            // #571, Astra B1: a cut from a transmit check must start the same
            // hold the controller's own cut does. Request for the reflected
            // source raises the notice; without the watcher's reading it says
            // so. The hook fires on the requesting thread before ConfirmDown's
            // wait, so the next press finds keying held.
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            try
            {
                ReflectedCutNotice? seen = null;
                TransmitKillSwitch.ReflectedCutHappened = n => seen = n;
                using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
                {
                    Assert.True(TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox));
                    TransmitKillSwitch.Request(TransmitKillSwitch.Source.ReflectedPower, "cut sentence");
                }
                Assert.NotNull(seen);
                Assert.False(seen!.Value.HasReading);
                Assert.Equal("ANT1", seen.Value.Antenna);   // the bare rig's TX antenna reads ANT1
                Assert.Equal(TransmitSafety.ReflectedCut.None, seen.Value.Rung);
            }
            finally { try { rig.Dispose(); } catch { } }
        }

        [Fact]
        public void TheHoldIsToldTheCutSentenceIsOut_AfterItIsToldTheCutHappened()
        {
            // Opus N4: the hold begins before the kill switch speaks, and the
            // kill switch's sentence is Urgent, which flushes the queue. So the
            // hold holds its VOX note back until this second hook, raised after
            // whichever of the three sentences Request spoke.
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            try
            {
                var order = new List<string>();
                TransmitKillSwitch.ReflectedCutHappened = n => order.Add("happened");
                TransmitKillSwitch.ReflectedCutSpoken = () => order.Add("spoken");
                using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
                {
                    TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox);
                    TransmitKillSwitch.Request(TransmitKillSwitch.Source.ReflectedPower, "cut sentence");
                }
                Assert.Equal(new[] { "happened", "spoken" }, order);

                // Not for the other sources.
                order.Clear();
                using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
                {
                    TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox);
                    TransmitKillSwitch.Request(TransmitKillSwitch.Source.EscapeKey);
                }
                Assert.Empty(order);
            }
            finally { try { rig.Dispose(); } catch { } }
        }

        [Fact]
        public void OtherKillSourcesDoNotStartAHold()
        {
            // Only the reflected-power cut (Noel, 2026-10-22 05:14): Escape and
            // a host request end a check without holding anything.
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            try
            {
                int notices = 0;
                TransmitKillSwitch.ReflectedCutHappened = n => notices++;
                using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
                {
                    TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox);
                    TransmitKillSwitch.Request(TransmitKillSwitch.Source.EscapeKey);
                }
                using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
                {
                    TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox);
                    TransmitKillSwitch.Request(TransmitKillSwitch.Source.HostRequest);
                }
                Assert.Equal(0, notices);
            }
            finally { try { rig.Dispose(); } catch { } }
        }

        [Fact]
        public void TheRaiseRefusalIsPerThread()
        {
            // Two checks raising at once must not read each other's sentence.
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            try
            {
                TransmitKillSwitch.TransmitHeld = door => "Held on " + System.Threading.Thread.CurrentThread.ManagedThreadId;
                string onOther = null;
                using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
                {
                    var t = new System.Threading.Thread(() =>
                    {
                        TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox);
                        onOther = TransmitKillSwitch.LastRaiseRefusal;
                    });
                    t.Start(); t.Join();
                    Assert.Null(TransmitKillSwitch.LastRaiseRefusal);   // this thread raised nothing
                    TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Mox);
                    Assert.NotNull(onOther);
                    Assert.NotEqual(onOther, TransmitKillSwitch.LastRaiseRefusal);
                    Assert.EndsWith(System.Threading.Thread.CurrentThread.ManagedThreadId.ToString(), TransmitKillSwitch.LastRaiseRefusal);
                }
            }
            finally { try { rig.Dispose(); } catch { } }
        }

        [Fact]
        public void AHoldHookThatThrowsOrIsAbsentNeverHoldsACheck()
        {
            // Noel's worry runs this way: a check refused because a hook broke
            // is "I can't transmit because an alarm hasn't cleared". Absent and
            // throwing both read as not held.
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            try
            {
                using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
                {
                    Assert.Null(TransmitKillSwitch.TransmitHeld);
                    Assert.True(TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Tune));
                }
                using (TransmitKillSwitch.Arm(rig, "the transmitter check"))
                {
                    TransmitKillSwitch.TransmitHeld = door => throw new InvalidOperationException("gone");
                    Assert.True(TransmitKillSwitch.RaiseCarrier(rig, TransmitKillSwitch.Carrier.Tune));
                    Assert.Null(TransmitKillSwitch.LastRaiseRefusal);
                }
            }
            finally { try { rig.Dispose(); } catch { } }
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
