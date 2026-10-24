using System;
using System.IO;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #699: a hard kill drops the carrier AND disarms every transmit source
    /// this computer generates, so nothing is left that could key the radio
    /// again or park the operator's microphone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The behavioural half runs against a real <see cref="FlexBase"/> with no
    /// radio behind it — the player and the tone generator are the rig's own
    /// objects, and <c>TxFileStart</c>/<c>TxToneStart</c> engage them without
    /// a connection. The ordering half is read from source, because
    /// <c>KillTransmitNow</c> needs a dispatcher and <c>Request</c> needs an
    /// armed check; what is pinned is that the disarm sits between the carrier
    /// drops and anything that waits.
    /// </para>
    /// <para>
    /// Bench check 4 in #699 — a normal send completing with VOX on must not
    /// double-fire into an error — is settled here by construction: the second
    /// stop on an already-releasing player returns early.
    /// </para>
    /// </remarks>
    public sealed class KillDisarmsSourcesTests
    {
        private static FlexBase Rig() => new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        [Fact]
        public void The_disarm_releases_an_engaged_voice_file()
        {
            FlexBase rig = null;
            try
            {
                rig = Rig();
                rig.TxFilePlayer.Load(new float[48000], 48000, "cq");
                rig.TxFileStart();
                Assert.True(rig.TxFilePlaying, "positive control: the file engaged");

                TransmitKillSwitch.DisarmSources(rig);

                Assert.False(rig.TxFilePlaying, "the kill left the voice file engaged — the thing that can re-key the radio survived the stop");
            }
            finally { try { rig?.Dispose(); } catch { } }
        }

        [Fact]
        public void The_disarm_releases_an_engaged_test_tone()
        {
            using var fixture = new RigOnVendorRadio("tone-disarm-test");
            fixture.AddOwnSlice(0, 14.25, "USB");
            fixture.Rig.q.MainLoop = true;
            Assert.True(fixture.Rig.TxToneStart(), "positive control: the tone engaged");
            Assert.True(fixture.Rig.TxToneEngaged);

            TransmitKillSwitch.DisarmSources(fixture.Rig);

            Assert.False(fixture.Rig.TxToneEngaged, "the kill left the test tone engaged");
        }

        [Fact]
        public void The_disarm_is_a_no_op_with_nothing_armed_and_with_no_rig()
        {
            // Bench check 3: a hard kill with nothing armed must not grow a
            // new failure mode.
            TransmitKillSwitch.DisarmSources(null);
            FlexBase rig = null;
            try
            {
                rig = Rig();
                Assert.False(rig.TxFilePlaying);
                Assert.False(rig.TxToneEngaged);
                TransmitKillSwitch.DisarmSources(rig);
                TransmitKillSwitch.DisarmSources(rig);   // twice: idempotent
                Assert.False(rig.TxFilePlaying);
                Assert.False(rig.TxToneEngaged);
            }
            finally { try { rig?.Dispose(); } catch { } }
        }

        [Fact]
        public void A_send_that_has_already_stopped_is_not_disturbed_by_the_disarm()
        {
            // Bench check 4 by construction: VoiceMessageSend.Finish calls
            // FileStop; a kill arriving in the same window calls it again.
            FlexBase rig = null;
            try
            {
                rig = Rig();
                rig.TxFilePlayer.Load(new float[48000], 48000, "cq");
                rig.TxFileStart();
                rig.TxFileStop();                     // the send's own road out
                Assert.False(rig.TxFilePlaying);
                TransmitKillSwitch.DisarmSources(rig); // the kill, a tick later
                Assert.False(rig.TxFilePlaying);
            }
            finally { try { rig?.Dispose(); } catch { } }
        }

        [Fact]
        public void KillTransmitNow_disarms_between_the_carrier_drops_and_the_request()
        {
            string body = MethodBody(Read("JJFlexWpf", "PttSafetyController.cs"),
                "public void KillTransmitNow(string source, string? spokenOutcome, string? subject)");
            int mox = body.IndexOf("DropCarrier(rig, TransmitKillSwitch.Carrier.Mox)", StringComparison.Ordinal);
            int tune = body.IndexOf("DropCarrier(rig, TransmitKillSwitch.Carrier.Tune)", StringComparison.Ordinal);
            int disarm = body.IndexOf("TransmitKillSwitch.DisarmSources(rig)", StringComparison.Ordinal);
            int request = body.IndexOf("TransmitKillSwitch.Request(", StringComparison.Ordinal);
            Assert.True(mox > 0 && tune > mox, "the two carrier drops have moved; re-anchor this rule");
            Assert.True(disarm > tune, "KillTransmitNow does not disarm the transmit sources after dropping the carriers (#699)");
            Assert.True(request > disarm, "the disarm must come before the request, which can wait on ConfirmDown");
        }

        [Fact]
        public void The_check_kill_disarms_too_and_before_anything_that_waits()
        {
            string body = MethodBody(Read("Radios", "TransmitKillSwitch.cs"),
                "public static void Request(Source source, string spokenOverride = null)");
            int tune = body.IndexOf("DropCarrier(rig, Carrier.Tune)", StringComparison.Ordinal);
            int disarm = body.IndexOf("DisarmSources(rig)", StringComparison.Ordinal);
            int confirm = body.IndexOf("ConfirmDown(rig)", StringComparison.Ordinal);
            Assert.True(tune > 0, "the carrier drops have moved; re-anchor this rule");
            Assert.True(disarm > tune, "Request drops the carriers and leaves the sources armed (#699)");
            Assert.True(confirm > disarm, "the disarm must precede the confirmation wait");
        }

        // ── machinery ───────────────────────────────────────────────────

        private static string Read(params string[] parts)
        {
            string path = Path.Combine(IntegrationPassTree.Root, Path.Combine(parts));
            Assert.True(File.Exists(path), path + " has moved; this rule is scanning nothing.");
            return File.ReadAllText(path);
        }

        private static string MethodBody(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "Could not find '" + signature + "'; re-anchor this rule rather than letting it pass on nothing.");
            int open = src.IndexOf('{', at + signature.Length);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                    return src.Substring(at, i - at + 1);
            }
            Assert.Fail("Unbalanced braces after '" + signature + "'.");
            return string.Empty;
        }
    }
}
