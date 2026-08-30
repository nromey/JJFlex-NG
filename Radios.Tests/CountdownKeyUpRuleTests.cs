using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 42 integration pass, #261. Pins WHEN the Fixer keys the radio
    /// during its countdown, and pins it to the one place that derives it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ruled by Noel 2026-08-30:</b> the radio must already be transmitting
    /// when the landing sounds. MOX does not engage the instant it is
    /// commanded, so the key-up goes out on the LAST COUNTING DIT and the
    /// count's final second absorbs the latency. The landing then means "you
    /// are transmitting", not "you are about to" — which is what the Earcon
    /// Explorer has always told the operator.
    /// </para>
    /// <para>
    /// <b>Why a test, and why this shape.</b> Three separate artifacts stated
    /// the rule — <c>EarconPlayer.CountdownLastDitAtMs</c> with the reasoning in
    /// its own doc comment, <c>FixerTransmitAudioBoundary.DefaultCountdownKeyUpMs</c>
    /// as the fallback, and the Explorer's operator-facing description — and the
    /// running code did something else, 1,240 ms later, at an instant nobody had
    /// ever described. It was wrong from the day it was written.
    /// </para>
    /// <para>
    /// <b>Nothing could fail.</b> The derivation and the sound live in
    /// assemblies that cannot see each other; no build, no merge and no test
    /// noticed. The orphan had ZERO CALLERS, which is the tell: a constant that
    /// describes itself as "the moment the transmit checks issue their key-up"
    /// and that no transmit check calls is a rule nobody is following.
    /// </para>
    /// <para>
    /// So this file asserts the wiring, not the number. Pinning 2000 would go
    /// stale the moment the beat is retuned — and retuning the beat is exactly
    /// what #396 did, which is how the old coupling drifted in the first place.
    /// </para>
    /// </remarks>
    public sealed class CountdownKeyUpRuleTests
    {
        private const string Player = "JJFlexWpf/EarconPlayer.cs";
        private const string Dialog = "JJFlexWpf/Dialogs/FixerDialog.cs";

        /// <summary>
        /// The rule has exactly one home, and it derives rather than states.
        /// </summary>
        [Fact]
        public void TheKeyUpMomentIsDerivedFromTheCountAndTheInterval()
        {
            string source = Read(Player);

            var derived = new Regex(
                @"CountdownLastDitAtMs\s*=>\s*\(\s*CountdownCounts\s*-\s*1\s*\)\s*\*\s*CountdownIntervalMs");

            Assert.True(derived.IsMatch(source),
                "EarconPlayer.CountdownLastDitAtMs no longer derives the key-up moment from "
                + "CountdownCounts and CountdownIntervalMs. If it now states a literal, it will "
                + "drift the next time the beat is retuned — which is precisely how the previous "
                + "coupling broke, silently, in a constant that decides when a transmitter keys.");
        }

        /// <summary>
        /// THE ORPHAN CHECK. This is the one that would have caught it.
        /// </summary>
        [Fact]
        public void TheKeyUpMomentHasACallerThatActuallyKeysTheRadio()
        {
            string source = Read(Dialog);

            Assert.Contains("EarconPlayer.CountdownLastDitAtMs", source, StringComparison.Ordinal);

            // And prove it is the value the key-up hand-off returns, not merely
            // mentioned in a comment somewhere in the file.
            var wired = new Regex(
                @"CountdownKeyUpMs\s*\(\s*\)[\s\S]{0,1200}?EarconPlayer\.CountdownLastDitAtMs");

            Assert.True(wired.IsMatch(source),
                "FixerDialog.CountdownKeyUpMs() no longer derives the key-up from "
                + "EarconPlayer.CountdownLastDitAtMs. A second derivation of the same instant is "
                + "how this broke before: two rules, 1,240 ms apart, no conflict, no failure, and "
                + "the one carrying the documented reasoning had no callers at all.");
        }

        /// <summary>
        /// The fallback must agree with the rule. It is what runs when the
        /// figure cannot be read, and a fallback that disagrees is a second
        /// answer wearing a safety label.
        /// </summary>
        [Fact]
        public void TheConservativeFallbackAgreesWithTheRule()
        {
            Assert.Equal(
                2000,
                Radios.ChainChecks.FixerTransmitAudioBoundary.DefaultCountdownKeyUpMs);
        }

        /// <summary>
        /// THE KEY-UP AND THE COUNTDOWN'S LENGTH ARE DIFFERENT NUMBERS, and the
        /// spoken stage needs both.
        /// </summary>
        /// <remarks>
        /// Conflating them is what put "speak now" on top of the countdown: the
        /// key-up lands on the last dit so the radio is up by the landing, and
        /// the landing then sounds for another second and a half. A stage that
        /// treats MOX confirmation as "the count is over" measures its own
        /// landing as the operator's voice, through a live microphone in the
        /// same room as the speaker playing it.
        /// </remarks>
        [Fact]
        public void TheCountdownOutlastsTheKeyUpAndTheFallbacksSaySo()
        {
            Assert.True(
                Radios.ChainChecks.FixerTransmitAudioBoundary.DefaultCountdownDurationMs
                > Radios.ChainChecks.FixerTransmitAudioBoundary.DefaultCountdownKeyUpMs,
                "the countdown's length is no longer later than its key-up, which would mean "
                + "the radio is asked to key after the sound has already finished");

            Assert.True(
                Radios.ChainChecks.FixerTransmitAudioBoundary.DefaultCountdownSettleMs > 0,
                "the settle between the countdown's last sound and the spoken cue is gone, so "
                + "the operator is asked to talk the instant the landing stops ringing");
        }

        /// <summary>
        /// The spoken stage waits the sound out BEFORE cueing. Order matters,
        /// so this asserts the sequence, not merely the presence of a call.
        /// </summary>
        [Fact]
        public void TheSpokenStageWaitsOutTheCountdownBeforeAskingAnyoneToSpeak()
        {
            string source = Read("Radios/ChainChecks/FixerTransmitAudioBoundary.cs");

            int wait = source.IndexOf("WaitOutTheCountdown()", StringComparison.Ordinal);
            int cue = source.IndexOf("Witness(_speakNow", StringComparison.Ordinal);

            Assert.True(wait > 0,
                "the spoken stage no longer waits for the countdown to finish sounding, so the "
                + "cue and the measurement start while the landing is still playing");
            Assert.True(cue > 0, "the spoken cue is gone entirely");
            Assert.True(wait < cue,
                "the spoken cue now happens BEFORE the countdown has finished sounding. The "
                + "order is the whole fix: wait the sound out, settle, then ask for a voice.");
        }

        /// <summary>
        /// The length is published by the host beside the sound, not restated.
        /// Same discipline as the key-up, and for the same reason.
        /// </summary>
        [Fact]
        public void TheHostPublishesTheCountdownLengthDerivedFromTheSound()
        {
            string dialog = Read(Dialog);

            Assert.Contains("countdownDurationMs:", dialog, StringComparison.Ordinal);
            Assert.Contains("EarconPlayer.CountdownDurationMs(", dialog, StringComparison.Ordinal);

            var literal = new Regex(@"countdownDurationMs:\s*\d+");
            Assert.False(literal.IsMatch(dialog),
                "the countdown's length is passed as a literal. It has to be asked of the sound, "
                + "or it drifts the next time the beat is retuned — which is exactly how the "
                + "key-up coupling broke.");
        }

        /// <summary>
        /// The positive control. Every assertion above is a source scan, and a
        /// scan that reads the wrong file — or a regex that matches nothing —
        /// passes for the wrong reason. Prove the reader finds something known
        /// to be present, and that it discriminates against something known not
        /// to be.
        /// </summary>
        [Fact]
        public void TheSourceReaderFindsWhatIsThereAndNotWhatIsNot()
        {
            string player = Read(Player);
            string dialog = Read(Dialog);

            Assert.Contains("CountdownIntervalMs", player, StringComparison.Ordinal);
            Assert.Contains("CountdownKeyUpMs", dialog, StringComparison.Ordinal);

            Assert.DoesNotContain("NoSuchCountdownSymbol", player, StringComparison.Ordinal);
            Assert.DoesNotContain("NoSuchCountdownSymbol", dialog, StringComparison.Ordinal);
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot find "
                + "its subject proves nothing about it.");
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
