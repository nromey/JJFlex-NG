using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A listenback take in a session whose PC microphone could not open
    /// recorded an empty sent track and said nothing about why: the transmit
    /// pipeline object still exists in that session, so the take built a sent
    /// writer on it, and the test tone went out on a path of its own that the
    /// writer never sees. Now the take writes a reason instead, as it already
    /// did for the radio's microphone jack and a missing pipeline. Pure
    /// decision plus the one call site that feeds it; no radio, no window.
    /// </summary>
    public sealed class ListenbackSentAbsentReasonTests
    {
        [Fact]
        public void A_PC_microphone_that_opened_records_the_sent_track()
        {
            Assert.Null(FlexBase.SentAbsentReason("PC", pipelinePresent: true, microphoneOpened: true, toneHasOwnPath: false));
        }

        [Fact]
        public void A_PC_microphone_that_did_not_open_gives_a_reason_rather_than_an_empty_track()
        {
            string withTone = FlexBase.SentAbsentReason("PC", pipelinePresent: true, microphoneOpened: false, toneHasOwnPath: true);
            Assert.NotNull(withTone);
            Assert.Contains("did not open", withTone);
            Assert.Contains("test tone", withTone);

            string noTone = FlexBase.SentAbsentReason("PC", pipelinePresent: true, microphoneOpened: false, toneHasOwnPath: false);
            Assert.NotNull(noTone);
            Assert.Contains("did not open", noTone);
            // The two say different things: one tone went out unrecorded, the
            // other could not go out at all.
            Assert.NotEqual(withTone, noTone);
        }

        [Fact]
        public void The_existing_reasons_are_unchanged()
        {
            Assert.Equal("radio microphone jack; DAX MIC is outside this build",
                FlexBase.SentAbsentReason("BAL", pipelinePresent: true, microphoneOpened: true, toneHasOwnPath: false));
            Assert.Equal("PC pipeline unavailable",
                FlexBase.SentAbsentReason("PC", pipelinePresent: false, microphoneOpened: false, toneHasOwnPath: false));
            // A missing pipeline is reported as such even when the microphone
            // also failed: the earlier fact is the one that explains the take.
            Assert.Equal("PC pipeline unavailable",
                FlexBase.SentAbsentReason("PC", pipelinePresent: false, microphoneOpened: false, toneHasOwnPath: true));
        }

        /// <summary>
        /// The decision is only as good as what the take start hands it: the
        /// session's real microphone state and its tone path, and a sent
        /// writer built only when the decision says so.
        /// </summary>
        [Fact]
        public void The_take_start_asks_with_the_sessions_own_microphone_state()
        {
            string source = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "Radios", "FlexBase.Listenback.cs"));
            string start = SourceMember.CSharp(source, "public async Task<ListenbackRun> StartListenbackAsync(");
            Assert.Contains("SentAbsentReason(MicSource, pipeline != null, opusInputAvailable, _standaloneTx != null)", start);
            Assert.Contains("if (sentAbsent == null) sent = new SentAudioWriter(pipeline, directory);", start);
            Assert.Contains("else writer.Header.Write(\"sent-absent-reason\", sentAbsent);", start);
            // Exactly one place builds a sent writer, so nothing bypasses it.
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(start, @"new SentAudioWriter\(").Count);
        }
    }
}
