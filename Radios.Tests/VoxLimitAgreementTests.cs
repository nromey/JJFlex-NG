using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The VOX delay is spoken in milliseconds and sent to the radio as a
    /// step count, and this refuses to let the conversion between the two
    /// drift away from what the radio actually does (#565).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shape of the defect this exists for.</b> FlexLib takes
    /// <c>SimpleVOXDelay</c> as a raw 0-100 and documents each step as 20 ms,
    /// so the radio's whole scale is 0-2000 ms. Jim wrote exactly that range
    /// into <c>FlexBase.VoxDelayMax</c> and then divided by 50, from the
    /// repository's first commit until 2026-09-07. Nothing threw and nothing
    /// logged: a value the operator typed still reached the radio, just at
    /// two-fifths of the hang time they asked for, and 2000 ms — the top of
    /// the box — landed on raw 40, leaving the upper sixty percent of the
    /// radio's scale unreachable from this application. A radio parked at its
    /// genuine maximum by other software read back as 5000 ms. The number in
    /// the ear was not the number on the air, and no test could see it,
    /// because both ends clamp and both ends were individually correct.
    /// </para>
    /// <para>
    /// <b>What is pinned.</b> The ratio <c>VoxDelayMax / VoxDelayMS</c> must
    /// equal the raw ceiling FlexLib clamps to. That single assertion is the
    /// one that would have failed for eight months, and it is the reason this
    /// file exists; the rest keeps the surfaces that restate the range — the
    /// fields panel and the audio layer take theirs from the constants, and
    /// the export multiplies by the same constant — from growing a second
    /// number of their own.
    /// </para>
    /// <para>
    /// The FlexLib half is read from SOURCE, the same way
    /// <c>TNFLimitAgreementTests</c> reads its dialog: the thing being checked
    /// is a literal a person wrote in a vendored file, and constructing a
    /// <c>Radio</c> to probe its clamp needs a network the test does not have.
    /// </para>
    /// </remarks>
    public sealed class VoxLimitAgreementTests
    {
        private const string FlexLibRadioSource = "FlexLib_API/FlexLib/Radio.cs";
        private const string ProfileReporterSource = "Radios/ProfileReporter.cs";

        /// <summary>
        /// The one that matters. FlexLib clamps the raw delay at 100 and says
        /// each step is 20 ms; our top of range divided by our step size must
        /// land exactly on that clamp — no lower (scale unreachable), no
        /// higher (values the radio silently clips).
        /// </summary>
        [Fact]
        public void The_delay_range_reaches_exactly_the_radio_ceiling()
        {
            int rawCeiling = SimpleVOXDelayClampIn(FlexLibRadioSource);
            int msPerStep = SimpleVOXDelayMsPerStepIn(FlexLibRadioSource);

            Assert.True(FlexBase.VoxDelayMS == msPerStep,
                "FlexBase.VoxDelayMS is " + FlexBase.VoxDelayMS + " ms per raw step but FlexLib "
                + "documents " + msPerStep + ". Every millisecond figure this application "
                + "speaks or exports for VOX delay is multiplied by this constant, so a "
                + "disagreement means the operator hears a hang time the radio is not using.");

            Assert.True(FlexBase.VoxDelayMax % FlexBase.VoxDelayMS == 0,
                "FlexBase.VoxDelayMax (" + FlexBase.VoxDelayMax + ") is not a whole number "
                + "of raw steps at " + FlexBase.VoxDelayMS + " ms each; the top of the box "
                + "would round to a value the operator never asked for.");

            Assert.True(FlexBase.VoxDelayMax / FlexBase.VoxDelayMS == rawCeiling,
                "FlexBase.VoxDelayMax / VoxDelayMS is " + (FlexBase.VoxDelayMax / FlexBase.VoxDelayMS)
                + " but FlexLib clamps SimpleVOXDelay at " + rawCeiling + ". Below the clamp, "
                + "part of the radio's scale is unreachable from here; above it, the box "
                + "displays hang times the radio silently clips. Either way the displayed "
                + "value stops being the value on the air, and nothing else notices.");
        }

        /// <summary>
        /// 100 ms per press is Jim's design and it must stay a whole number of
        /// raw steps, or a press would move the radio by a fraction it rounds
        /// away and the box would climb while the radio stood still.
        /// </summary>
        [Fact]
        public void A_press_moves_the_radio_by_whole_steps()
        {
            Assert.True(FlexBase.VoxDelayIncrement % FlexBase.VoxDelayMS == 0,
                "VoxDelayIncrement (" + FlexBase.VoxDelayIncrement + " ms) is not a multiple of "
                + "VoxDelayMS (" + FlexBase.VoxDelayMS + "); a press would send a step count "
                + "the integer division rounds, and the box would disagree with the radio.");
            Assert.True(FlexBase.VoxDelayMin == 0,
                "VoxDelayMin is " + FlexBase.VoxDelayMin + "; the radio's scale starts at 0.");
        }

        /// <summary>
        /// The gain is raw 0-100 with no scaling, so its limits are the
        /// radio's own; pinned so a well-meant "percent" cannot creep in.
        /// </summary>
        [Fact]
        public void The_gain_range_is_the_radio_scale()
        {
            Assert.Equal(0, FlexBase.VoxGainMin);
            Assert.Equal(100, FlexBase.VoxGainMax);
            Assert.True(FlexBase.VoxGainIncrement > 0 && FlexBase.VoxGainIncrement <= FlexBase.VoxGainMax);
        }

        /// <summary>
        /// The #227 export prints the delay in milliseconds from the raw
        /// value. Until 2026-09-07 it multiplied by a literal 50 in two places,
        /// so the export was printing the wrong figure independently of
        /// FlexBase. It must multiply by the constant, and never by a literal.
        /// </summary>
        [Fact]
        public void The_export_multiplies_by_the_constant_not_a_literal()
        {
            string source = Read(ProfileReporterSource);
            var byConstant = Regex.Matches(source, @"SimpleVOXDelay\s*\*\s*FlexBase\.VoxDelayMS");
            var byLiteral = Regex.Matches(source, @"SimpleVOXDelay\s*\*\s*\d+");
            Assert.True(byConstant.Count >= 2,
                "Expected at least two `SimpleVOXDelay * FlexBase.VoxDelayMS` sites in "
                + ProfileReporterSource + " (the plain-text section and the key/value form) "
                + "and found " + byConstant.Count + ". If the export stopped reporting VOX "
                + "delay, delete this assertion on purpose rather than letting it rot.");
            Assert.True(byLiteral.Count == 0,
                ProfileReporterSource + " multiplies SimpleVOXDelay by a literal in "
                + byLiteral.Count + " place(s). Use FlexBase.VoxDelayMS; a literal here is "
                + "how the export printed 50 ms per step for eight months after FlexLib said 20.");
        }

        /// <summary>
        /// Positive control for the source scan. A checker that cannot find
        /// its subject reports agreement for the same reason it would report
        /// anything else: nothing. This asserts the scan reads values it could
        /// not have guessed.
        /// </summary>
        [Fact]
        public void The_scan_can_actually_read_flexlib()
        {
            Assert.Equal(100, SimpleVOXDelayClampIn(FlexLibRadioSource));
            Assert.Equal(20, SimpleVOXDelayMsPerStepIn(FlexLibRadioSource));
        }

        /// <summary>
        /// The upper clamp inside <c>SimpleVOXDelay</c>'s setter:
        /// <c>if (new_val &gt; 100) new_val = 100;</c>. Scoped to that property's
        /// body so a neighbouring clamp cannot answer for it.
        /// </summary>
        private static int SimpleVOXDelayClampIn(string relative)
        {
            string body = SimpleVOXDelayBody(relative);
            var m = Regex.Match(body, @"if\s*\(\s*new_val\s*>\s*(\d+)\s*\)\s*new_val\s*=\s*(\d+)\s*;");
            Assert.True(m.Success,
                "No upper clamp of the form `if (new_val > N) new_val = N;` inside "
                + "SimpleVOXDelay in " + relative + ". FlexLib changed shape; re-read "
                + "the setter and repoint this scan, because until then it checks nothing.");
            Assert.Equal(m.Groups[1].Value, m.Groups[2].Value);
            return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The milliseconds per step, from the doc comment immediately above
        /// the property: "The delay will be (value * 20) milliseconds". The
        /// comment is the vendor's only statement of the unit — the command
        /// on the wire carries the raw number and the radio does the scaling.
        /// </summary>
        private static int SimpleVOXDelayMsPerStepIn(string relative)
        {
            string source = Read(relative);
            int at = source.IndexOf("public int SimpleVOXDelay", StringComparison.Ordinal);
            Assert.True(at >= 0, "No `public int SimpleVOXDelay` in " + relative + ".");
            string above = source.Substring(Math.Max(0, at - 600), Math.Min(600, at));
            var m = Regex.Match(above, @"\(\s*value\s*\*\s*(\d+)\s*\)\s*milliseconds");
            Assert.True(m.Success,
                "The doc comment above SimpleVOXDelay in " + relative + " no longer says "
                + "`(value * N) milliseconds`. That comment is the vendor's only statement "
                + "of the unit; find where it went before trusting VoxDelayMS.");
            return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string SimpleVOXDelayBody(string relative)
        {
            string source = Read(relative);
            int at = source.IndexOf("public int SimpleVOXDelay", StringComparison.Ordinal);
            Assert.True(at >= 0, "No `public int SimpleVOXDelay` in " + relative + ".");
            // The property body is short; 1200 characters comfortably covers
            // the getter, the clamp and the send without reaching the next
            // property's clamp.
            return source.Substring(at, Math.Min(1200, source.Length - at));
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that "
                + "cannot find its subject proves nothing about it.");
            return File.ReadAllText(path);
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
