using System.Windows.Forms;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The mode branch behind every message key (Sprint 48 Track A, #151):
    /// one slot, two payloads, and the radio's mode picks which goes out.
    /// </summary>
    /// <remarks>
    /// Six combinations — text only, audio only, both — each in CW and in a
    /// voice mode, plus the two "nothing for this mode" cases, which must be
    /// named outcomes and never silence. Pure, so no radio, window or key
    /// table is needed; what it does NOT prove is that pressing the key on a
    /// real build reaches this branch — see the track report for the keys to
    /// press.
    /// </remarks>
    public sealed class MessageKeyDispatchTests
    {
        private static CWMessageItem Slot(string text, string audio) =>
            new(Keys.D1 | Keys.Control, text, "CQ", audio);

        private const string Cq = "CQ CQ DE K5NER";
        private const string Take = "take-2026-10-05-101500";

        // ── CW mode ───────────────────────────────────────────────────────

        [Fact]
        public void InCwATextOnlySlotSendsCw()
            => Assert.Equal(MessageKeyAction.SendCw, MessageKeyDispatch.Plan(Slot(Cq, ""), "CW"));

        [Fact]
        public void InCwABothSlotSendsCwNotTheRecording()
            => Assert.Equal(MessageKeyAction.SendCw, MessageKeyDispatch.Plan(Slot(Cq, Take), "CW"));

        [Fact]
        public void InCwAnAudioOnlySlotSaysItHasNothingForCw()
            => Assert.Equal(MessageKeyAction.NothingForCw, MessageKeyDispatch.Plan(Slot("", Take), "CW"));

        // ── Voice modes ───────────────────────────────────────────────────

        [Theory]
        [InlineData("USB")]
        [InlineData("LSB")]
        [InlineData("AM")]
        [InlineData("FM")]
        [InlineData("DIGU")]
        public void InAVoiceModeAnAudioOnlySlotSendsTheRecording(string mode)
            => Assert.Equal(MessageKeyAction.SendVoice, MessageKeyDispatch.Plan(Slot("", Take), mode));

        [Fact]
        public void InAVoiceModeABothSlotSendsTheRecordingNotTheText()
            => Assert.Equal(MessageKeyAction.SendVoice, MessageKeyDispatch.Plan(Slot(Cq, Take), "USB"));

        [Fact]
        public void InAVoiceModeATextOnlySlotSaysItHasNothingForVoice()
            => Assert.Equal(MessageKeyAction.NothingForVoice, MessageKeyDispatch.Plan(Slot(Cq, ""), "USB"));

        // ── The mode word ─────────────────────────────────────────────────

        /// <summary>
        /// CW is matched the way the transmit gate and TxTonePathTrouble
        /// match it: by prefix, ignoring case. A sideband-flavoured CW string
        /// from any future source is still CW.
        /// </summary>
        [Theory]
        [InlineData("CW")]
        [InlineData("cw")]
        [InlineData("CWL")]
        [InlineData("CWU")]
        public void CwIsRecognisedByPrefixIgnoringCase(string mode)
            => Assert.True(MessageKeyDispatch.IsCwMode(mode));

        /// <summary>
        /// No slice, no mode: the voice path is taken and its own path check
        /// says what is missing. The branch must never read an empty mode as
        /// CW and queue text to a keyer nobody can hear.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void AnUnknownModeIsNotCw(string? mode)
        {
            Assert.False(MessageKeyDispatch.IsCwMode(mode));
            Assert.Equal(MessageKeyAction.NothingForVoice, MessageKeyDispatch.Plan(Slot(Cq, ""), mode));
            Assert.Equal(MessageKeyAction.SendVoice, MessageKeyDispatch.Plan(Slot("", Take), mode));
        }

        /// <summary>
        /// Whitespace is not a payload. A slot whose text is a space must read
        /// as text-less, or the CW branch would key a space and announce a
        /// send that transmitted nothing.
        /// </summary>
        [Fact]
        public void WhitespaceIsNotAPayload()
        {
            var slot = Slot("   ", "  ");
            Assert.False(slot.HasText);
            Assert.False(slot.HasAudio);
            Assert.Equal(MessageKeyAction.NothingForCw, MessageKeyDispatch.Plan(slot, "CW"));
            Assert.Equal(MessageKeyAction.NothingForVoice, MessageKeyDispatch.Plan(slot, "USB"));
        }

        /// <summary>
        /// The three-argument constructor Jim's VB side has always used still
        /// works and yields an audio-less slot: older callers and older
        /// operator files are text-only by construction.
        /// </summary>
        [Fact]
        public void TheOriginalConstructorMakesATextOnlySlot()
        {
            var slot = new CWMessageItem(Keys.D1 | Keys.Control, Cq, "CQ");
            Assert.True(slot.HasText);
            Assert.False(slot.HasAudio);
            Assert.Equal(string.Empty, slot.Audio);
        }
    }
}
