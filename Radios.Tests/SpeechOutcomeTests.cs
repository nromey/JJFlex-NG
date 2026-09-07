#nullable enable
using System;
using System.IO;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  The tri-state outcome (#521) and the client binding's pure parts.
    //
    //  Nothing here touches NVDA. The classification table and the SSML
    //  builder are the two places a wrong assumption would turn a refusal
    //  into a false "heard" or a healthy sentence into INVALID_PARAMETER,
    //  and both are pinned without a reader on the desk. The shipped DLL
    //  is checked the way the README tells a human to check it — with
    //  speakText as the positive control — so the record and the binary
    //  cannot drift apart silently.
    // ────────────────────────────────────────────────────────────────
    public class SpeechOutcomeTests
    {
        [Fact]
        public void OnlyCompletedIsHeard()
        {
            Assert.True(SpeechOutcome.Completed(3, 3, 500).WasHeard);
            Assert.False(SpeechOutcome.Cancelled(1, 3, byUs: true, 200).WasHeard);
            Assert.False(SpeechOutcome.Unknown(SpeechUnknownReason.Timeout, null, 9000).WasHeard);
        }

        [Fact]
        public void UnknownMustCarryAReason()
        {
            Assert.Throws<ArgumentException>(() => SpeechOutcome.Unknown(SpeechUnknownReason.None, "blank", 0));
        }

        [Fact]
        public void ToString_SaysWhoCancelled()
        {
            Assert.Contains("by us", SpeechOutcome.Cancelled(2, 5, byUs: true, 300).ToString());
            Assert.Contains("NOT by us", SpeechOutcome.Cancelled(2, 5, byUs: false, 300).ToString());
            Assert.Contains("word 2 of 5", SpeechOutcome.Cancelled(2, 5, byUs: false, 300).ToString());
        }

        // ── Return-code classification (nvdaControllerClient) ──

        [Fact]
        public void Classify_ZeroIsCompleted_WithEveryMark()
        {
            var o = NvdaControllerClient.Classify(0, marksReached: 27, markCount: 28, cancelledByUs: false, 8079);
            Assert.Equal(SpeechOutcomeKind.Completed, o.Kind);
            // Completed means every word was reached whatever the tally said;
            // the last mark can race the done signal by a millisecond.
            Assert.Equal(28, o.MarksReached);
            Assert.Equal(8079, o.ElapsedMs);
        }

        [Fact]
        public void Classify_1223IsCancelled_AndAttributionComesFromUs()
        {
            var ours = NvdaControllerClient.Classify(1223, 3, 12, cancelledByUs: true, 1200);
            var theirs = NvdaControllerClient.Classify(1223, 3, 12, cancelledByUs: false, 1200);
            Assert.Equal(SpeechOutcomeKind.Cancelled, ours.Kind);
            Assert.True(ours.CancelledByUs);
            Assert.False(theirs.CancelledByUs);
            Assert.Equal(3, theirs.MarksReached);
        }

        [Theory]
        [InlineData(5u, SpeechUnknownReason.Refused)]
        [InlineData(87u, SpeechUnknownReason.InvalidSsml)]
        [InlineData(1717u, SpeechUnknownReason.ChannelAbsent)]
        [InlineData(1722u, SpeechUnknownReason.ChannelAbsent)]
        [InlineData(1726u, SpeechUnknownReason.ChannelAbsent)]
        [InlineData(1818u, SpeechUnknownReason.Escaped)]
        [InlineData(31u, SpeechUnknownReason.Other)]
        public void Classify_EverythingElseIsUnknown_WithTheRightReason(uint rc, SpeechUnknownReason reason)
        {
            var o = NvdaControllerClient.Classify(rc, 0, 4, false, 10);
            Assert.Equal(SpeechOutcomeKind.Unknown, o.Kind);
            Assert.Equal(reason, o.UnknownReason);
            Assert.False(o.WasHeard);
            Assert.Contains(rc.ToString(), o.Detail);
        }

        // ── SSML ──

        [Fact]
        public void BuildSsml_OneMarkPerWord_NamedByTicket()
        {
            string ssml = NvdaCompletionChannel.BuildSsml("Connected to FLEX-8600, SmartLink, 4 slices.", 42, out int marks);
            Assert.Equal(6, marks);
            Assert.StartsWith("<speak><mark name=\"t42w0\"/>Connected ", ssml);
            Assert.Contains("<mark name=\"t42w5\"/>slices.</speak>", ssml);
            Assert.DoesNotContain("  ", ssml);
        }

        [Fact]
        public void BuildSsml_EscapesEveryXmlSpecial()
        {
            string ssml = NvdaCompletionChannel.BuildSsml("S 3 < 5 & Tom's \"radio\" > mine", 1, out _);
            Assert.Contains("&lt;", ssml);
            Assert.Contains("&amp;", ssml);
            Assert.Contains("&apos;", ssml);
            Assert.Contains("&quot;", ssml);
            Assert.Contains("&gt;", ssml);
            // Nothing raw survives between the tags.
            string body = ssml.Replace("<speak>", "").Replace("</speak>", "");
            foreach (var piece in body.Split(new[] { "<mark name=\"" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string afterTag = piece.Substring(piece.IndexOf("\"/>", StringComparison.Ordinal) + 3);
                Assert.DoesNotContain("<", afterTag);
                Assert.DoesNotContain(">", afterTag);
                Assert.DoesNotContain("\"", afterTag);
                Assert.DoesNotContain("'", afterTag);
            }
        }

        [Fact]
        public void BuildSsml_CollapsesWhitespace_AndCountsWordsAPersonWould()
        {
            NvdaCompletionChannel.BuildSsml("  PC   audio\ton.\n", 7, out int marks);
            Assert.Equal(3, marks);
        }

        [Theory]
        [InlineData("t42w0", true, 42L, 0)]
        [InlineData("t9223372036854775807w27", true, long.MaxValue, 27)]
        [InlineData("test", false, 0L, 0)]
        [InlineData("w3", false, 0L, 0)]
        [InlineData("t42w", false, 0L, 0)]
        [InlineData("tw3", false, 0L, 0)]
        [InlineData("", false, 0L, 0)]
        [InlineData(null, false, 0L, 0)]
        public void TryParseMark_RoundTripsOurs_AndRejectsOthers(string? mark, bool ok, long ticket, int index)
        {
            Assert.Equal(ok, NvdaCompletionChannel.TryParseMark(mark, out long t, out int i));
            if (ok)
            {
                Assert.Equal(ticket, t);
                Assert.Equal(index, i);
            }
        }

        [Fact]
        public void MarkName_ParsesBackToItself()
        {
            string name = NvdaCompletionChannel.MarkName(123456789, 17);
            Assert.True(NvdaCompletionChannel.TryParseMark(name, out long t, out int i));
            Assert.Equal(123456789, t);
            Assert.Equal(17, i);
        }

        // ── The shipped DLL, checked the way the README says to ──

        [Theory]
        [InlineData("win-x64")]
        [InlineData("win-x86")]
        public void ShippedClient_HasSpeakSsml_AndTheRecordMatchesTheBinary(string arch)
        {
            string root = RepoRoot();
            string dll = Path.Combine(root, "runtimes", arch, "native", "nvdaControllerClient.dll");
            Assert.True(File.Exists(dll), $"expected the shipped client at {dll}");

            byte[] bytes = File.ReadAllBytes(dll);
            string ascii = System.Text.Encoding.ASCII.GetString(bytes);

            // POSITIVE CONTROL: a genuine client of any age exports speakText.
            // If this fails, the search is broken, not the DLL.
            Assert.Contains("nvdaController_speakText", ascii);
            Assert.Contains("nvdaController_speakSsml", ascii);
            Assert.Contains("nvdaController_setOnSsmlMarkReachedCallback", ascii);
            // Absent for the 2026.2 client; arrives with NvdaController3 in 2026.3.
            Assert.DoesNotContain("nvdaController_isSpeaking", ascii);

            // The README's recorded hash must be THIS file's hash, or the
            // record has drifted from the binary — the defect class this
            // repository's native README exists to prevent.
            string readme = File.ReadAllText(Path.Combine(root, "runtimes", "win-x64", "native", "README.md"));
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            Assert.Contains(sha, readme, StringComparison.OrdinalIgnoreCase);

            // The licence rides beside it.
            Assert.True(File.Exists(Path.Combine(root, "runtimes", arch, "native", "nvdaControllerClient.LICENSE.txt")));
        }

        [Fact]
        public void ShippedClient_IsNotResolvedByName_Anywhere()
        {
            // The whole point of #541's lesson: nothing may bind this DLL by
            // its bare name, because search order is what finds the wrong
            // copy. NativeLoader must not map it; no DllImport may name it.
            string root = RepoRoot();
            string loader = File.ReadAllText(Path.Combine(root, "NativeLoader.vb"));
            Assert.DoesNotContain("nvdaControllerClient", loader, StringComparison.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Radios"), "*.cs", SearchOption.AllDirectories))
            {
                string src = File.ReadAllText(file);
                Assert.DoesNotContain("DllImport(\"nvdaControllerClient", src, StringComparison.OrdinalIgnoreCase);
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
