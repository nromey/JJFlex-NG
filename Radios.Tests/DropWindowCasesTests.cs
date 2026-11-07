using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #625, repair round two: the drop window opens after ANY connection
    /// loss, including while only the standing diagnostic log (on by default)
    /// is running. Its words must fit what was actually recording, and say
    /// "failed part way through" only when the file may really be short.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class DropWindowCasesTests
    {
        private const string PathZ = @"C:\Traces\one.zip";

        private static CaptureArchiveNotice Notice(bool detailedCapture, bool tailUncertain) =>
            new CaptureArchiveNotice("6300inshack", PathZ, successorOpened: true, archivedSessionId: Guid.NewGuid(),
                tailUncertain: tailUncertain, sinkFailedBeforeDrop: false, recordingNow: () => true,
                detailedCapture: detailedCapture);

        [Fact]
        public void A_detailed_capture_gets_the_capture_wording_and_no_failure_claim_when_complete()
        {
            var n = Notice(detailedCapture: true, tailUncertain: false);
            string text = n.Explanation;
            Assert.Contains("was lost while JJ Flexible Access was recording a capture", text, StringComparison.Ordinal);
            Assert.Contains("You made it to describe a problem you were experiencing", text, StringComparison.Ordinal);
            Assert.DoesNotContain("failed part way through", text, StringComparison.Ordinal);
            Assert.DoesNotContain("even if it is incomplete", text, StringComparison.Ordinal);
            Assert.Contains("send the file to the developer", text, StringComparison.Ordinal);
        }

        [Fact]
        public void The_standing_log_alone_claims_no_capture_and_no_reason_it_was_made()
        {
            var n = Notice(detailedCapture: false, tailUncertain: false);
            string text = n.Explanation;
            Assert.Contains("was keeping its diagnostic log", text, StringComparison.Ordinal);
            Assert.DoesNotContain("capture", text, StringComparison.Ordinal);   // lower case: the file's "Capture Note" marker is a name, not a claim
            Assert.DoesNotContain("You made it", text, StringComparison.Ordinal);
            Assert.DoesNotContain("failed part way through", text, StringComparison.Ordinal);
            // Still where the file is and who to send it to.
            Assert.Contains("copy the path to this file", text, StringComparison.Ordinal);
            Assert.Contains("send the file to the developer", text, StringComparison.Ordinal);
            Assert.DoesNotContain(PathZ, text, StringComparison.Ordinal);   // the path has its own control
            Assert.Contains(PathZ, n.AsText(), StringComparison.Ordinal);

            // Unnamed radio too.
            var plain = new CaptureArchiveNotice("", PathZ, true, Guid.NewGuid(), false, false, () => true, detailedCapture: false);
            Assert.Equal("Connection to the radio was lost while JJ Flexible Access was keeping its diagnostic log.", plain.WhatHappened);
        }

        [Fact]
        public void A_short_tail_is_said_as_such_for_a_capture_and_for_the_log()
        {
            var capture = Notice(detailedCapture: true, tailUncertain: true);
            Assert.Contains("failed part way through", capture.Explanation, StringComparison.Ordinal);   // the approved paragraph
            Assert.Contains("may be incomplete", capture.Explanation, StringComparison.Ordinal);

            var log = Notice(detailedCapture: false, tailUncertain: true);
            string text = log.Explanation;
            Assert.Contains("may be incomplete", text, StringComparison.Ordinal);
            Assert.Contains("the last lines of this log may not have reached the disk", text, StringComparison.Ordinal);
            Assert.DoesNotContain("capture", text, StringComparison.Ordinal);   // lower case: the file's "Capture Note" marker is a name, not a claim
        }

        [Fact]
        public void The_archive_carries_whether_a_capture_was_running_to_the_window()
        {
            // The fact exists at the trace boundary (EndedDetailedCapture);
            // this pins that it now reaches the notice and is not defaulted.
            string root = FlexBasePartials.RepoRoot();
            string archive = File.ReadAllText(Path.Combine(root, "Radios", "CaptureArchive.cs"));
            Assert.Contains("detailedCapture: result.EndedDetailedCapture", archive, StringComparison.Ordinal);
            string globals = File.ReadAllText(Path.Combine(root, "globals.vb"));
            Assert.Contains("outcome.EndedDetailedCapture = result.EndedDetailedCapture", globals, StringComparison.Ordinal);
        }
    }
}
