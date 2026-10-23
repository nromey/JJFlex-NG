#nullable enable
using System.IO;
using System.Text.RegularExpressions;
using Radios.Tests.Fixtures;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #800, Phase 2 as settled 2026-10-22: a down/up pair on the lock key is
    /// COUNTED, not guarded against. The measured streams go through the
    /// detector verbatim and none of them counts; only an unmeasured
    /// synthetic pair does, which is the positive control that the detector
    /// can see what it is for.
    /// </summary>
    public sealed class LockKeyPairDetectorTests
    {
        private static int Replay(LockKeyPairDetector d, params JawsShiftSpaceCapture20261022.KeyEvent[][] streams)
        {
            int counted = 0;
            foreach (var stream in streams)
                foreach (var e in stream)
                {
                    if (e.Down)
                    {
                        if (e.IsShiftSpace && !e.Repeat) d.NoteDown(e.Tick);   // as MainWindow.LockKeyDown does: repeats never reach it
                    }
                    else if (d.NoteUp(e.Tick)) counted++;
                }
            return counted;
        }

        [Fact]
        public void The_measured_JAWS_taps_count_nothing()
        {
            var d = new LockKeyPairDetector();
            Assert.Equal(0, Replay(d, JawsShiftSpaceCapture20261022.HomeTaps));
            Assert.Equal(0, d.ImplausiblePairs);
        }

        [Fact]
        public void The_measured_JAWS_hold_counts_nothing()
        {
            // One down, thirty repeats, one up 1,484 ms later: a human hold.
            var d = new LockKeyPairDetector();
            Assert.Equal(31, JawsShiftSpaceCapture20261022.HoldDowns);
            Assert.Equal(0, Replay(d, JawsShiftSpaceCapture20261022.HomeHold));
            Assert.Equal(0, d.ImplausiblePairs);
        }

        [Fact]
        public void The_whole_measured_Home_stream_counts_nothing()
        {
            var d = new LockKeyPairDetector();
            int counted = 0;
            foreach (var e in JawsShiftSpaceCapture20261022.HomeStreamInOrder())
            {
                if (e.Down) { if (e.IsShiftSpace && !e.Repeat) d.NoteDown(e.Tick); }
                else if (d.NoteUp(e.Tick)) counted++;
            }
            Assert.Equal(0, counted);
        }

        [Fact]
        public void A_plain_Space_release_with_no_lock_press_in_flight_is_ignored()
        {
            var d = new LockKeyPairDetector();
            Assert.False(d.NoteUp(JawsShiftSpaceCapture20261022.PlainSpace[1].Tick));
            Assert.Equal(0, d.ImplausiblePairs);
        }

        [Fact]
        public void A_synthetic_pair_is_counted_and_nothing_else_happens()
        {
            // Positive control. NOT a measured stream on this key: see the
            // fixture. The detector returns true so a caller could trace it;
            // no caller changes behaviour on the answer.
            var d = new LockKeyPairDetector();
            Assert.Equal(1, Replay(d, JawsShiftSpaceCapture20261022.SyntheticPairNotMeasuredOnThisKey));
            Assert.Equal(1, d.ImplausiblePairs);
        }

        [Fact]
        public void The_threshold_is_the_controllers_push_to_talk_threshold()
        {
            // One number for one question. The controller's constant is private;
            // read it from source so the two cannot drift apart silently.
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "PttSafetyController.cs"));
            var m = Regex.Match(src, @"private const int ImplausibleReleaseMs = (\d+);");
            Assert.True(m.Success, "PttSafetyController.ImplausibleReleaseMs is missing");
            Assert.Equal(int.Parse(m.Groups[1].Value), LockKeyPairDetector.ImplausibleReleaseMs);
        }

        [Fact]
        public void Every_measured_tap_is_above_the_threshold_with_room_to_spare()
        {
            // The shortest measured human tap on this key was 62 ms. If the
            // threshold ever rose to meet it, a real tap would be counted as a
            // pair; this says how much room there is.
            foreach (var tap in JawsShiftSpaceCapture20261022.HomeTaps)
            {
                long held = tap[1].Tick - tap[0].Tick;
                Assert.True(held >= LockKeyPairDetector.ImplausibleReleaseMs + 10,
                    "a measured tap of " + held + " ms sits within 10 ms of the " + LockKeyPairDetector.ImplausibleReleaseMs + " ms threshold");
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return System.AppContext.BaseDirectory;
        }
    }
}
