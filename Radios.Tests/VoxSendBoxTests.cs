using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The one rule that decides what the VOX send box does to a send (#835), held
    /// at every combination of its four inputs.
    /// </summary>
    /// <remarks>
    /// What this cannot see: whether the doors read the inputs correctly at the
    /// press (the box from the saved settings, VOX from the radio, the
    /// operator's push to talk from the controller). Those are read in
    /// <c>VoiceMessageSender.RouteFor</c> and pressed at the bench.
    /// </remarks>
    public sealed class VoxSendBoxTests
    {
        [Fact]
        public void OffByDefault()
        {
            // Nothing in this test process sets the preference, so this reads
            // the initializer. A test that ever does set it must put it back.
            Assert.False(VoxSendBox.Ticked);
            Assert.Equal(SendRoute.AsBefore, VoxSendBox.Choose(ticked: false, voiceVoxOn: true, transmitting: false, operatorHolds: false));
        }

        [Fact]
        public void TickedWithVoxOnAndAQuietRadioTheBoxKeys()
        {
            Assert.Equal(SendRoute.Box, VoxSendBox.Choose(true, true, false, false));
        }

        /// <summary>15:28: a radio held up only by VOX counts as unkeyed for the box.</summary>
        [Fact]
        public void TickedWithVoxOnAndTheRadioHeldUpOnlyByVoxTheBoxStillKeys()
        {
            Assert.Equal(SendRoute.Box, VoxSendBox.Choose(true, true, transmitting: true, operatorHolds: false));
        }

        /// <summary>#151: if the operator is transmitting, the send never touches the key, box or no box.</summary>
        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void WhenTheOperatorHoldsTheTransmitterTheSendJoinsIt(bool ticked, bool vox)
        {
            Assert.Equal(SendRoute.JoinTransmission, VoxSendBox.Choose(ticked, vox, transmitting: true, operatorHolds: true));
        }

        /// <summary>15:25: unticked with VOX on, Play does not send; you key by voice or the lock.</summary>
        [Fact]
        public void UntickedWithVoxOnAndAQuietRadioEverythingIsAsBefore()
        {
            Assert.Equal(SendRoute.AsBefore, VoxSendBox.Choose(false, true, false, false));
        }

        /// <summary>15:25 again: unticked, keyed by VOX, Play while keyed sends into that transmission.</summary>
        [Fact]
        public void UntickedAndKeyedByVoxTheSendJoinsTheTransmission()
        {
            Assert.Equal(SendRoute.JoinTransmission, VoxSendBox.Choose(false, true, transmitting: true, operatorHolds: false));
        }

        /// <summary>Bench step 4: box on, VOX off, nothing changes from today.</summary>
        [Theory]
        [InlineData(false, SendRoute.AsBefore)]
        [InlineData(true, SendRoute.JoinTransmission)]
        public void TickedWithVoxOffTheBoxDoesNothing(bool transmitting, SendRoute expected)
        {
            Assert.Equal(expected, VoxSendBox.Choose(true, voiceVoxOn: false, transmitting, operatorHolds: false));
        }

        [Fact]
        public void TheVoxTailWaitCoversTheLongestVoxDelay()
        {
            Assert.True(VoxSendBox.VoxTailDropWait.TotalMilliseconds > FlexBase.VoxDelayMax);
        }
    }
}
