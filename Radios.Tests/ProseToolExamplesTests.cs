#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The prose tool's "Reads as" examples for the Fixer (#390) must read the
    /// way the code says them. "A sample that lies invites a fix to a bug that
    /// does not exist": a track nearly made three wrong edits chasing these.
    /// Where the words come from a helper, the example is compared with the
    /// helper's own output; where the code formats a number inline, the
    /// example is held to that format. Reads <c>tools/prose/surfaces/fixer.json</c>
    /// from the working copy; nothing is written.
    /// </summary>
    public sealed class ProseToolExamplesTests
    {
        private static Dictionary<string, string> Examples()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string path = Path.Combine(dir!.FullName, "tools", "prose", "surfaces", "fixer.json");
            Assert.True(File.Exists(path), "not found: " + path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var examples = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.GetProperty("examples").EnumerateObject())
                examples[p.Name] = p.Value.GetString() ?? "";
            return examples;
        }

        [Fact]
        public void The_host_api_examples_are_what_the_helper_says()
        {
            var e = Examples();
            string phrase = AudioSetupCheck.HostApiPhrase(e["hostApi"]);
            Assert.Equal(phrase, e["HostApiPhrase"]);
            // TransmitStages joins it with ", through " inside the one hole.
            Assert.Equal(", through " + phrase, e["fixer.finding.microphone.text-2#HostApiPhrase"]);
        }

        [Fact]
        public void The_noise_gate_examples_are_numbers_in_the_codes_own_format()
        {
            // FixerDialog writes each as float.ToString("0.#", InvariantCulture)
            // and puts " dB" or " LUFS" after it.
            var e = Examples();
            foreach (string key in new[] { "threshold", "Threshold", "NoiseFloorLufs", "ThresholdMarginDb",
                         "fixer.window.describe-gate-derivation.text-2#threshold" })
            {
                Assert.True(float.TryParse(e[key], NumberStyles.Float, CultureInfo.InvariantCulture, out float v),
                    key + " is not a number: " + e[key]);
                Assert.Equal(v.ToString("0.#", CultureInfo.InvariantCulture), e[key]);
            }
            // The derived threshold is the floor plus the margin, as the sentence says.
            Assert.Equal(float.Parse(e["NoiseFloorLufs"], CultureInfo.InvariantCulture)
                         + float.Parse(e["ThresholdMarginDb"], CultureInfo.InvariantCulture),
                float.Parse(e["threshold"], CultureInfo.InvariantCulture));
            // The default sentence is the gate's own default.
            Assert.Equal(JJPortaudio.TxNoiseGate.DefaultThresholdDb.ToString("0.#", CultureInfo.InvariantCulture),
                e["fixer.window.describe-gate-derivation.text-2#threshold"]);
        }

        [Fact]
        public void The_channel_and_sample_rate_examples_are_what_the_code_can_say()
        {
            var e = Examples();
            // "across {OpenChannels} channels" is said only above two channels, as digits.
            Assert.True(int.TryParse(e["OpenChannels"], NumberStyles.None, CultureInfo.InvariantCulture, out int ch) && ch > 2,
                "OpenChannels must be digits above two: " + e["OpenChannels"]);
            Assert.True(int.TryParse(e["OpenSampleRateHz"], NumberStyles.None, CultureInfo.InvariantCulture, out int hz),
                "OpenSampleRateHz is written with ToString(\"0\"): " + e["OpenSampleRateHz"]);
            Assert.Equal(hz.ToString("0", CultureInfo.InvariantCulture) + " Hz",
                e["fixer.finding.evidence.text-5#OpenSampleRateHz"]);
        }

        [Fact]
        public void The_yes_or_no_example_is_one_the_code_writes()
        {
            // Every {yesOrNo} in the Fixer is (x ? "yes" : "no").
            Assert.Contains(Examples()["yesOrNo"], new[] { "yes", "no" });
        }
    }
}
