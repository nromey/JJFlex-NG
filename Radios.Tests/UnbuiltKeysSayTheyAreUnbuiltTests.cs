using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #829: two default keys did nothing and said nothing. Alt+Shift+X (DX
    /// cluster) reached a placeholder that only wrote a trace line, and
    /// Ctrl+Shift+F2 (resume the scan) can never find a paused scan, because
    /// nothing calls PauseScan. Each now says it is not available. Read from
    /// source because both live in the VB app, which no test project loads.
    /// </summary>
    public sealed class UnbuiltKeysSayTheyAreUnbuiltTests
    {
        private static string Globals() => File.ReadAllText(Path.Combine(
            FieldKeyMapScan.RepoRoot(), "globals.vb"));

        private static string Body(string code, string start, string end)
        {
            int from = code.IndexOf(start, StringComparison.Ordinal);
            Assert.True(from >= 0, start + " was not found in globals.vb");
            int to = code.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Assert.True(to > from, end + " was not found after " + start);
            return code.Substring(from, to - from);
        }

        // The lexicon lookup as it appears in the VB source. Whether each key
        // exists in the store is LexiconKeyCoverageTests' job, which reads
        // globals.vb as well.
        private static string SpokenKey(string key) =>
            // Split so LexiconKeyCoverageTests does not read this helper as a
            // lookup of its own.
            "Radios.Lexicon" + ".Get(" + '"' + key + '"' + ")";

        [Fact]
        public void The_DX_cluster_key_says_it_is_not_available()
        {
            string body = Body(Globals(), "Friend Sub ShowArCluster()", "End Sub");
            Assert.Contains(SpokenKey("connect.cluster.not_available"), body);
            Assert.Contains("Radios.ScreenReaderOutput.Speak(", body);
            Assert.Contains("Radios.Speech.SpeechSubject.UnbuiltKey", body);
        }

        [Fact]
        public void Resume_scan_says_so_when_no_scan_is_paused_and_still_resumes_a_paused_one()
        {
            string body = Body(Globals(), ".ResumeScan = Sub()", "End Sub,");
            // The paused test is the one scan.vb's resumeScan itself uses.
            Assert.Contains("(scanstate <> scans.none) AndAlso (Not ScanInProcess)", body);
            int notPaused = body.IndexOf("If Not scanIsPaused Then", StringComparison.Ordinal);
            int spoken = body.IndexOf(SpokenKey("connect.scan.resume_none_paused"), StringComparison.Ordinal);
            int resumes = body.IndexOf("scan.resumeScan()", StringComparison.Ordinal);
            Assert.True(notPaused >= 0, "the key must test whether a scan is paused");
            Assert.True(spoken > notPaused && spoken < resumes,
                "the not-paused branch must speak the sentence, before and apart from the resume");
            Assert.True(resumes > spoken, "a genuinely paused scan must still resume");
            Assert.Contains("Radios.Speech.SpeechSubject.UnbuiltKey", body);
        }
    }
}
