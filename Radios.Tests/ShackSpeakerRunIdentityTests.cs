using System;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The shack speaker is given back at the end of the PC-audio thread's
    /// teardown, and until Track G5 that write carried no identity for which
    /// audio run it belonged to: it asked only whether a radio object existed,
    /// was connected and was not disconnecting.
    ///
    /// <para>That is not enough, because an audio thread can outlive its own
    /// run. <c>stopRemoteAudioThread</c> ABANDONS the thread when its ten-
    /// second join fails, the setter returns, and a later switch to PC audio
    /// starts a NEW thread that mutes the speaker on purpose. When the
    /// abandoned thread finally resumes its teardown, every one of those three
    /// checks passes — so it unmuted the shack speaker underneath a live PC
    /// audio run. Astra's review of Tracks G3 and G4 named this as a concrete
    /// allowed thread interleaving, not a hardware result.</para>
    ///
    /// <para>The decision is a pure function of the run identity and the
    /// connection, so it is tested here with no radio, no thread and no audio
    /// hardware — the same shape as <c>ClassifyPendingStreamWait</c>.</para>
    /// </summary>
    public sealed class ShackSpeakerRunIdentityTests
    {
        private const long Mine = 7;

        [Fact]
        public void TheRunThatIsStillTheCurrentOne_GivesTheSpeakerBack()
        {
            Assert.Null(FlexBase.ShackSpeakerRestoreRefusal(
                Mine, currentRun: Mine, radioPresent: true, connected: true, disconnecting: false));
        }

        [Fact]
        public void AnAbandonedRunThatWakesUpUnderANewerOne_LeavesTheSpeakerAlone()
        {
            // The whole point: every connection check below passes, and the
            // write is still wrong, because the newer run muted the speaker
            // deliberately and is using it.
            string why = FlexBase.ShackSpeakerRestoreRefusal(
                Mine, currentRun: Mine + 1, radioPresent: true, connected: true, disconnecting: false);
            Assert.NotNull(why);
            Assert.Contains("newer", why, StringComparison.Ordinal);
        }

        [Fact]
        public void AnAbandonedRunWithNoSuccessor_StillGivesTheSpeakerBack()
        {
            // PC audio was switched off and nothing started since. Nobody else
            // owns the speaker, so the ruled outcome — "unmute it if you're
            // not using it" — is this run's to carry out after all.
            Assert.Null(FlexBase.ShackSpeakerRestoreRefusal(
                Mine, currentRun: Mine, radioPresent: true, connected: true, disconnecting: false));
        }

        [Theory]
        [InlineData(false, true, false)]   // no radio
        [InlineData(true, false, false)]   // not connected
        [InlineData(true, true, true)]     // teardown has begun
        public void TheConnectionGuardIsKept_AndTheRunCheckIsAnAdditionToIt(
            bool radioPresent, bool connected, bool disconnecting)
        {
            string why = FlexBase.ShackSpeakerRestoreRefusal(
                Mine, currentRun: Mine, radioPresent, connected, disconnecting);
            Assert.NotNull(why);
            Assert.Contains("closing", why, StringComparison.Ordinal);
        }

        [Fact]
        public void TheRunIdentityIsCheckedBeforeTheConnection_SoASupersededRunNeverReadsAsAWrite()
        {
            // A superseded run on a live connection is the dangerous case; it
            // must be refused for being superseded, not left to a connection
            // check that happily passes.
            string why = FlexBase.ShackSpeakerRestoreRefusal(
                Mine, currentRun: Mine + 3, radioPresent: false, connected: false, disconnecting: true);
            Assert.NotNull(why);
            Assert.Contains("newer", why, StringComparison.Ordinal);
        }

        [Fact]
        public void TheTeardownWriteAsksThisQuestion_AndTheRunItAsksAboutIsItsOwn()
        {
            // The pure function only matters if the teardown calls it with the
            // run it was started for. remoteAudioProc takes that run as its
            // parameter, so there is no field for a newer run to have moved.
            string text = Source("Radios/FlexBase.cs");

            int start = text.IndexOf("private void startRemoteAudioThread()", StringComparison.Ordinal);
            Assert.True(start > 0);
            string starter = text.Substring(start, 900);
            Assert.Contains("Interlocked.Increment(ref _remoteAudioRun)", starter, StringComparison.Ordinal);
            Assert.Contains("remoteAudioProc(run)", starter, StringComparison.Ordinal);

            Assert.Contains("private void remoteAudioProc(long run)", text, StringComparison.Ordinal);

            int write = text.IndexOf("IsMuteLocalAudioWhenRemoteOn=false on remote audio stop", StringComparison.Ordinal);
            Assert.True(write > 0);
            string before = text.Substring(Math.Max(0, write - 600), Math.Min(600, write));
            // Both arguments pinned: this run's own number, against the
            // number of the run that is current NOW. Passing the same value
            // twice would compile, read as guarded, and check nothing.
            Assert.Contains("ShackSpeakerRestoreRefusal(run, Interlocked.Read(ref _remoteAudioRun)",
                before, StringComparison.Ordinal);
        }

        private static string Source(string relative)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        }
    }
}
