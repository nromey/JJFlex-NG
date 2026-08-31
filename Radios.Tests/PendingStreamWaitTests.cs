using System;
using Flex.Smoothlake.FlexLib;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Task #419: switching PC audio off terminated the application.
    ///
    /// <para><b>The defect.</b> remoteAudioProc waited for each remote-audio
    /// stream with a predicate of the shape
    /// <c>(stream != null) || Disconnecting || stopRemoteAudio</c> — three
    /// ways to become true, only one of which means the stream arrived. The
    /// wait's boolean collapses them, so the code branched on "the wait ended"
    /// and read it as "the stream is here". Switching PC audio off sets
    /// <c>stopRemoteAudio</c>, which ALWAYS satisfies the predicate, so build
    /// 4.1.16.1760 constructed <c>audioChannelData</c> from a null stream on
    /// demand — a NullReferenceException on a handler-less background thread,
    /// which took the whole process down. No dialog, no speech, the
    /// application simply gone.</para>
    ///
    /// <para><b>The fix.</b> The decision that was missing — did the thing I
    /// waited for actually arrive, and if not, why not — is
    /// <c>FlexBase.ClassifyPendingStreamWait</c>, a pure function of the
    /// state AFTER the wait. These tests pin it: a cancelled or disconnecting
    /// wait takes the failure path and never constructs the channel. And if a
    /// null ever reaches the constructors again by some new route, they now
    /// refuse it by name instead of crashing from inside a field
    /// assignment.</para>
    /// </summary>
    public sealed class PendingStreamWaitTests
    {
        // ------------------------------------------------------------------
        // The decision: what did the wait actually produce?
        // ------------------------------------------------------------------

        /// <summary>
        /// The crash case, verbatim: PC audio switched off while the stream
        /// was pending. The predicate was satisfied, the stream is null, and
        /// the only correct answer is "cancelled" — never "arrived".
        /// </summary>
        [Fact]
        public void Switching_PC_audio_off_is_a_cancellation_not_an_arrival()
        {
            var outcome = FlexBase.ClassifyPendingStreamWait(
                streamArrived: false, cancelled: true, disconnecting: false);

            Assert.Equal(FlexBase.PendingStreamWaitOutcome.CancelledByOperator, outcome);
        }

        /// <summary>
        /// The identical trap on the other disjunct: disconnect while the
        /// stream is pending must take the failure path too.
        /// </summary>
        [Fact]
        public void Disconnecting_while_the_stream_is_pending_is_not_an_arrival()
        {
            var outcome = FlexBase.ClassifyPendingStreamWait(
                streamArrived: false, cancelled: false, disconnecting: true);

            Assert.Equal(FlexBase.PendingStreamWaitOutcome.Disconnecting, outcome);
        }

        /// <summary>
        /// A stream that genuinely arrived proceeds — even when cancel or
        /// disconnect fired in the same window. Building the channel from a
        /// real stream is always safe, and the main loop and the remoteDone
        /// teardown handle the stop in order.
        /// </summary>
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void An_arrived_stream_wins_over_everything(bool cancelled, bool disconnecting)
        {
            var outcome = FlexBase.ClassifyPendingStreamWait(
                streamArrived: true, cancelled: cancelled, disconnecting: disconnecting);

            Assert.Equal(FlexBase.PendingStreamWaitOutcome.Arrived, outcome);
        }

        /// <summary>
        /// When the operator's own act and a disconnect land together, the
        /// operator's act is the truer story for the trace file.
        /// </summary>
        [Fact]
        public void Cancellation_outranks_disconnection_when_both_fired()
        {
            var outcome = FlexBase.ClassifyPendingStreamWait(
                streamArrived: false, cancelled: true, disconnecting: true);

            Assert.Equal(FlexBase.PendingStreamWaitOutcome.CancelledByOperator, outcome);
        }

        /// <summary>
        /// Only the wait that expired with NOTHING is the radio's fault. This
        /// is the one outcome that earns an Error in the trace; the two above
        /// are normal exits and log as Info.
        /// </summary>
        [Fact]
        public void A_wait_that_expired_empty_means_the_radio_never_answered()
        {
            var outcome = FlexBase.ClassifyPendingStreamWait(
                streamArrived: false, cancelled: false, disconnecting: false);

            Assert.Equal(FlexBase.PendingStreamWaitOutcome.NeverArrived, outcome);
        }

        // ------------------------------------------------------------------
        // The last line of defence: the constructors refuse a null by name
        // ------------------------------------------------------------------

        /// <summary>
        /// A null stream reaching the receive-channel constructor is a
        /// programming error in the caller whichever way it arrived. The old
        /// code answered it with a NullReferenceException thrown from a field
        /// assignment — anonymous, and fatal on remoteAudioProc's bare
        /// thread. A named refusal says whose mistake it was.
        /// </summary>
        [Fact]
        public void The_receive_channel_refuses_a_null_stream_by_name()
        {
            var ex = Assert.Throws<ArgumentNullException>(() =>
                new FlexBase.audioChannelData((RXRemoteAudioStream)null, "test"));

            Assert.Equal("stream", ex.ParamName);
        }

        /// <summary>Same refusal on the transmit side.</summary>
        [Fact]
        public void The_transmit_channel_refuses_a_null_stream_by_name()
        {
            var ex = Assert.Throws<ArgumentNullException>(() =>
                new FlexBase.audioChannelData((TXRemoteAudioStream)null, "test"));

            Assert.Equal("stream", ex.ParamName);
        }
    }
}
