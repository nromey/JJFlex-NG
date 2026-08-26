using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 35 Track G, #235. Locks the meters that the Speak Meters readout
    /// is allowed to consult.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a source test rather than a behavioural one.</b> The thing worth
    /// protecting here is not arithmetic — <c>SwrFromPower</c> already has its
    /// own tests, and every corrected meter is a plain property read. What
    /// needs protecting is the CHOICE OF INSTRUMENT, and that choice is a line
    /// of code, not a computable value. A behavioural test would need a live
    /// radio publishing two contradictory meters at once to tell the right
    /// answer from the wrong one, which is exactly the situation nobody can
    /// arrange on demand — the discrediting readings came off a bench 8600 on
    /// one afternoon in August.
    /// </para>
    /// <para>
    /// <c>MeterToneEngine</c> lives in JJFlexWpf, which this project does not
    /// reference and must not: constructing that assembly's types is what puts
    /// dialogs on the operator's desktop. Reading its source is the way to
    /// assert something about it from the safe test project.
    /// </para>
    /// <para>
    /// <b>What went wrong, so the next person understands what they are being
    /// stopped from doing.</b> <c>GetMeterSpeechSummary</c> feeds three
    /// surfaces — the Speak Meters key (Ctrl+Alt+V), its menu item, and the
    /// Status dialog's Meters section. Until 2026-08-26 all three read:
    /// </para>
    /// <para>
    /// <c>SWRValue</c> — the radio's own SWR meter, which reported 1.008 into
    /// an unterminated antenna port while 76% of the power was coming back.
    /// <c>ALC</c> — HWALC, the external-amplifier jack rather than transmit
    /// drive, guarded by a fraction-shaped threshold applied to a dBFS
    /// reading, so the line was in practice never spoken at all.
    /// <c>MicData</c> — the analog codec meter, which reads -120 for PC audio,
    /// so a PC-audio station was told "Mic -120" while the Audio Workshop two
    /// keystrokes away reported a healthy level.
    /// </para>
    /// <para>
    /// Every one of those had already been established as the wrong instrument
    /// on another surface. Three surfaces read these meters, one of them was
    /// wrong, and nothing detected it. This is the something that detects it
    /// recurring.
    /// </para>
    /// </remarks>
    public sealed class MeterSpeechSummarySourceTests
    {
        /// <summary>
        /// The meter reads that must never come back. Each is spelled with its
        /// <c>_rig.</c> receiver so the token cannot collide with a mention of
        /// the same property name in prose.
        /// </summary>
        private static readonly string[] DiscreditedReads =
        {
            "_rig.SWRValue",
            "_rig.ALC",
            "_rig.MicData",
        };

        /// <summary>
        /// The corrected reads. These are the positive control for the whole
        /// test: without them, a file that had simply been emptied — or a path
        /// that silently resolved to the wrong thing — would pass the absence
        /// check with flying colours.
        /// </summary>
        private static readonly string[] RequiredReads =
        {
            "_rig.ComputedSWR",
            "_rig.SwAlcDb",
            "_rig.ScMicRecentDb",
            "_rig.ScMicMaxDb",
        };

        private const string TargetFile = "JJFlexWpf/MeterToneEngine.cs";

        [Fact]
        public void SpeechSummaryDoesNotReadAnyDiscreditedMeter()
        {
            string raw = ReadTarget();
            string code = StripComments(raw);

            var found = DiscreditedReads
                .Where(token => code.Contains(token, StringComparison.Ordinal))
                .ToList();

            Assert.True(found.Count == 0,
                "MeterToneEngine reads meters the project has established are wrong: "
                + string.Join(", ", found)
                + ". Speak Meters is the most reflexive way an operator asks how transmit is "
                + "going, and a blind operator has no needle to sanity-check it against. Use "
                + "ComputedSWR, SwAlcDb and ScMicDb — see the remarks on "
                + "GetMeterSpeechSummary.");
        }

        [Fact]
        public void SpeechSummaryReadsTheCorrectedMeters()
        {
            string code = StripComments(ReadTarget());

            var absent = RequiredReads
                .Where(token => !code.Contains(token, StringComparison.Ordinal))
                .ToList();

            Assert.True(absent.Count == 0,
                "MeterToneEngine no longer reads: " + string.Join(", ", absent)
                + ". If the readout was deliberately restructured, update this test to name "
                + "wherever the corrected values now come from — but do not delete it, because "
                + "its absence check is the only thing standing between the raw meters and "
                + "three surfaces at once.");
        }

        /// <summary>
        /// The positive control on the comment stripper. The file deliberately
        /// discusses <c>_rig.ALC</c> in prose, explaining why it is not read.
        /// If the stripper stopped working, the absence test above would fail
        /// on that prose and look like a real regression; if the stripper
        /// became over-eager and returned nothing, the absence test would pass
        /// while checking nothing at all. This pins both ends.
        /// </summary>
        [Fact]
        public void CommentStripperRemovesProseButKeepsCode()
        {
            string raw = ReadTarget();
            string code = StripComments(raw);

            Assert.Contains("_rig.ALC", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("_rig.ALC", code, StringComparison.Ordinal);

            // The stripper must not have eaten the file. Real code survives.
            Assert.Contains("public static string GetMeterSpeechSummary()", code,
                StringComparison.Ordinal);
            Assert.True(code.Length > raw.Length / 4,
                "Stripping comments removed more than three quarters of the file ("
                + raw.Length + " to " + code.Length + " characters), which means it ate "
                + "code and the absence checks are verifying far less than they appear to.");
        }

        // ────────────────────────────────────────────────────────────────

        private static string ReadTarget()
        {
            string path = Path.Combine(RepoRoot(), TargetFile.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + TargetFile + " (looked at " + path + "). A test that cannot "
                + "find its subject passes every absence check it makes, which is the exact "
                + "failure shape this file exists to refuse.");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Drop <c>//</c> and <c>///</c> lines and the interiors of block
        /// comments, so prose ABOUT a discredited meter does not read as a use
        /// of one. Line-oriented on purpose: it is enough for this file, and a
        /// real C# lexer here would be a second thing to keep correct.
        /// </summary>
        private static string StripComments(string text)
        {
            var kept = new List<string>();
            bool inBlock = false;

            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.TrimStart();

                if (inBlock)
                {
                    if (trimmed.Contains("*/", StringComparison.Ordinal)) inBlock = false;
                    continue;
                }

                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

                if (trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    if (!trimmed.Contains("*/", StringComparison.Ordinal)) inBlock = true;
                    continue;
                }

                // A trailing comment on a line of code. Everything before it is
                // real, everything after it is prose.
                int slashes = line.IndexOf("//", StringComparison.Ordinal);
                kept.Add(slashes >= 0 ? line[..slashes] : line);
            }

            return string.Join("\n", kept);
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
