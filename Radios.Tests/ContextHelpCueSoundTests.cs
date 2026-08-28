using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The context-help availability cue must not sound like a toggle, must sit
    /// further back than anything else, and must not fire while the application
    /// is closing (#275).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It shipped in Sprint 36 and Noel heard it for the first time on
    /// 2026-08-27. He did not hear a new sound: "the double tone beepbeep that
    /// I'm used to... it was a toggle." The parameters agree with him. Feature
    /// on was Press, 500 → 750, two even 60 ms taps; the cue was Press,
    /// 660 → 880, two even 40 ms taps. Same voice, same contour, same count,
    /// same rhythm — the toggle transposed up a fourth.
    /// </para>
    /// <para>
    /// <b>What these tests can and cannot do.</b> They hold the SEPARATION —
    /// that the cue does not share the toggle's voice, rhythm or interval, and
    /// that it sits on its own loudness rung. They cannot tell you whether the
    /// result sounds good, or whether one cue per rested control reads as
    /// informative or as nagging. Only ears answer that, which is exactly how
    /// the first version shipped, and the report says so.
    /// </para>
    /// <para>
    /// Source-read: Radios.Tests cannot load the WPF assembly, and playing a
    /// sound in a test would put audio on the operator's machine.
    /// </para>
    /// </remarks>
    public class ContextHelpCueSoundTests
    {
        private const string EarconPlayer = "JJFlexWpf/EarconPlayer.cs";
        private const string CueSource = "JJFlexWpf/ContextHelpCue.cs";
        private const string MainWindow = "JJFlexWpf/MainWindow.xaml.cs";

        /// <summary>A parsed earcon: its voice, its notes and its loudness tier.</summary>
        private sealed record Earcon(string Voice, string Tier, IReadOnlyList<(int Hz, int Ms)> Steps)
        {
            /// <summary>The notes, gaps dropped.</summary>
            public IReadOnlyList<(int Hz, int Ms)> Notes =>
                Steps.Where(s => s.Hz > 0).ToList();

            /// <summary>True when every note is the same length — the shape the
            /// toggle vocabulary owns.</summary>
            public bool IsEvenlyPaced =>
                Notes.Count > 1 && Notes.Select(n => n.Ms).Distinct().Count() == 1;

            /// <summary>Ratio of the last note's pitch to the first's.</summary>
            public double Interval =>
                Notes.Count > 1 ? (double)Notes[^1].Hz / Notes[0].Hz : 1.0;
        }

        // ────────────────────────────────────────────────────────────────
        //  Prove the instrument before trusting it
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_toggle_is_what_an_even_pair_in_the_press_voice_looks_like()
        {
            // The positive control. Every assertion below is of the form "the
            // cue is not like the toggle", and that is worth nothing unless the
            // same measurements find the toggle guilty. If Feature on ever
            // stops reading as an even pair in the Press voice, these tests are
            // steering away from a landmark that has moved.
            var toggle = Read(EarconPlayer, "FeatureOnTone");

            Assert.Equal("Press", toggle.Voice);
            Assert.True(toggle.IsEvenlyPaced, "Feature on should be two notes of equal length");
            Assert.Equal(2, toggle.Notes.Count);
            Assert.Equal(1.5, toggle.Interval, 2);   // a perfect fifth, 500 → 750
        }

        [Fact]
        public void The_shape_that_shipped_and_was_rejected_would_fail_these_rules()
        {
            // The negative control, stated as data rather than as a memory:
            // Press voice, two even 40 ms taps, a perfect fourth. Every rule
            // below must reject it, or the rules are not the reason the sound
            // changed.
            var rejected = new Earcon("Press", "VolumeSoft",
                new[] { (660, 40), (0, 60), (880, 40) });

            Assert.Equal("Press", rejected.Voice);
            Assert.True(rejected.IsEvenlyPaced);
            Assert.Equal(4.0 / 3.0, rejected.Interval, 2);
        }

        // ────────────────────────────────────────────────────────────────
        //  Distinctness is interval, rhythm and timbre
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_cue_does_not_use_the_toggle_voice()
        {
            var cue = Read(EarconPlayer, "ContextHelpAvailableTone");
            var toggle = Read(EarconPlayer, "FeatureOnTone");

            Assert.NotEqual(toggle.Voice, cue.Voice);
        }

        [Fact]
        public void The_cue_is_not_a_pair_of_even_taps()
        {
            // The toggle vocabulary owns even pairs and even triples — Feature
            // on and off, All slices, the connect counting series. A cue that
            // means one specific thing cannot borrow that rhythm.
            var cue = Read(EarconPlayer, "ContextHelpAvailableTone");

            Assert.False(cue.IsEvenlyPaced,
                "the cue's notes are all the same length, which is the toggle family's rhythm");

            // And unequal by a margin the ear can hear, not by 5 ms.
            var lengths = cue.Notes.Select(n => n.Ms).ToList();
            Assert.True(lengths.Max() >= lengths.Min() * 3,
                $"the notes are {string.Join(" and ", lengths)} ms — too close to read as "
                + "an upbeat and an answer rather than as two taps");
        }

        [Fact]
        public void The_cue_opens_a_wider_interval_than_the_toggle()
        {
            var cue = Read(EarconPlayer, "ContextHelpAvailableTone");
            var toggle = Read(EarconPlayer, "FeatureOnTone");

            Assert.True(cue.Interval > toggle.Interval,
                $"the cue's interval ({cue.Interval:0.00}) must be wider than the toggle's "
                + $"({toggle.Interval:0.00}); it was a fourth, narrower than the toggle's fifth");

            // Still rising. Falling pairs already mean a toggle turned OFF, and
            // a collision of MEANING is worse than a collision of sound.
            Assert.True(cue.Interval > 1.0, "the cue must still rise");
        }

        [Fact]
        public void No_pitch_in_the_cue_sits_within_a_semitone_of_a_toggle_pitch()
        {
            // Near-misses are what make two sounds confusable — a pitch a
            // quarter-tone off reads as the same note played badly.
            const double Semitone = 1.0594630943592953;
            var cue = Read(EarconPlayer, "ContextHelpAvailableTone");
            int[] toggleFamily = { 500, 750, 625, 785, 940 };

            foreach (var (hz, _) in cue.Notes)
            {
                foreach (int other in toggleFamily)
                {
                    double ratio = hz > other ? (double)hz / other : (double)other / hz;
                    Assert.True(ratio >= Semitone,
                        $"{hz} Hz is less than a semitone from the toggle family's {other} Hz");
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Conspicuousness is the tier, and it is a separate complaint
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_cue_sits_on_its_own_rung_below_every_other_sound()
        {
            var cue = Read(EarconPlayer, "ContextHelpAvailableTone");
            Assert.Equal("VolumeFaint", cue.Tier);

            string source = Source(EarconPlayer);
            float faint = ReadTier(source, "VolumeFaint");
            float soft = ReadTier(source, "VolumeSoft");
            float normal = ReadTier(source, "VolumeNormal");

            Assert.True(faint < soft, "VolumeFaint must be quieter than VolumeSoft");

            // The tier set's own rule: about 2 dB a rung, so nothing jumps out
            // of the set and the ordering is still audible. 1.15 to 1.35 is
            // roughly 1.2 to 2.6 dB.
            double step = soft / faint;
            Assert.InRange(step, 1.15, 1.35);
            Assert.InRange(normal / soft, 1.15, 1.35);
        }

        // ────────────────────────────────────────────────────────────────
        //  The deciding rule survived the audition and must stay
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_cue_still_sounds_only_when_the_help_content_is_new()
        {
            // Noel did not object to WHEN it fires, only to how it sounds. The
            // rule that makes it rare by construction rather than merely quiet
            // is the one thing this rework must not have touched.
            string source = Source(CueSource);

            Assert.Contains("Decider.ShouldCue(", source, StringComparison.Ordinal);
            Assert.Contains("JJFlexHelp.FindExplanation(", source, StringComparison.Ordinal);
            Assert.Contains("Decider.NoteSpoken(", source, StringComparison.Ordinal);
        }

        // ────────────────────────────────────────────────────────────────
        //  It must not offer help on a surface that is disappearing
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Shutdown_disarms_the_cue_before_the_exit_sequence_runs()
        {
            // The exit sequence puts prompts up. Focus lands on one, the
            // operator reads it — which takes longer than the settle interval —
            // and the cue announces that help is available on a window that is
            // about to be gone. Disarming AFTER the callback would be too late,
            // so the ORDER is the assertion.
            string source = Source(MainWindow);

            int suspend = source.IndexOf("ContextHelpCue.SuspendForShutdown()", StringComparison.Ordinal);
            int callback = source.IndexOf("!AppExitCallback()", StringComparison.Ordinal);

            Assert.True(suspend >= 0, "MainWindow.RequestShutdown must disarm the context-help cue");
            Assert.True(callback >= 0, "the exit callback call moved — re-check this test");
            Assert.True(suspend < callback,
                "the cue must be disarmed BEFORE the exit sequence, because the exit prompt "
                + "is inside it and is the most likely thing focus is resting on");
        }

        [Fact]
        public void Declining_to_exit_gives_the_cue_back()
        {
            // A latch that only ever closes would silently kill the cue for the
            // rest of a session in which the operator changed their mind.
            string source = Source(MainWindow);
            Assert.Contains("ContextHelpCue.ResumeAfterCancelledShutdown()", source, StringComparison.Ordinal);
        }

        [Fact]
        public void A_detailed_capture_records_whether_the_cue_fired()
        {
            // Before this there was no trace on the earcon path at ANY level,
            // so a quiet log said nothing about whether the cue sounded — and a
            // 7,998-line Info trace with no earcon lines in it was read on
            // 2026-08-27 as though it were evidence. Verbose is the level a
            // detailed capture turns on and an ordinary session does not.
            string source = Source(CueSource);

            Assert.Contains("TraceLevel.Verbose", source, StringComparison.Ordinal);
            Assert.Matches(new Regex(@"sounding", RegexOptions.None), source);
        }

        // ────────────────────────────────────────────────────────────────
        //  Reading the source
        // ────────────────────────────────────────────────────────────────

        private static Earcon Read(string file, string method)
        {
            string body = MethodBody(Source(file), method);

            var voice = Regex.Match(body, @"EarconVoices\.(\w+)");
            Assert.True(voice.Success, $"{method}: no EarconVoices.<voice> found");

            var tier = Regex.Match(body, @"\b(Volume\w+)\b");
            Assert.True(tier.Success, $"{method}: no loudness tier found");

            var steps = Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*\)")
                .Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)))
                .ToList();
            Assert.True(steps.Count >= 2, $"{method}: expected a cadence, found {steps.Count} step(s)");

            return new Earcon(voice.Groups[1].Value, tier.Groups[1].Value, steps);
        }

        /// <summary>
        /// The body of a public static void method, from its signature to the
        /// closing brace at method indentation. Crude, and adequate: these
        /// earcons are four lines each and live in one file with one style.
        /// </summary>
        private static string MethodBody(string source, string method)
        {
            int at = source.IndexOf("public static void " + method + "()", StringComparison.Ordinal);
            Assert.True(at >= 0, "method not found: " + method);
            int end = source.IndexOf("\n        }", at, StringComparison.Ordinal);
            Assert.True(end > at, "could not find the end of " + method);
            return source[at..end];
        }

        private static float ReadTier(string source, string name)
        {
            var m = Regex.Match(source, @"internal const float " + name + @" = ([\d.]+)f;");
            Assert.True(m.Success, "loudness tier not found: " + name);
            return float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Source(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative);
            Assert.True(File.Exists(path), "source not found: " + path);
            return File.ReadAllText(path);
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
