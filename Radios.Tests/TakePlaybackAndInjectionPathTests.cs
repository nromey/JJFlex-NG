using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Two rules about the Audio Workshop's Hear Yourself category, both of
    /// them about a control describing something other than what it does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#455 — one question, asked of both stores.</b> Don, 2026-09-01,
    /// recorded a take, pressed Stop recording, pressed Play last take and was
    /// told there were no recordings — while Open recordings folder, one
    /// section away in the same dialog, showed the files and another program
    /// played them. Neither button was broken. They asked different stores: the
    /// folder button asked <c>RecordingStore</c>, the play button asked the
    /// radio's own quick-record buffer, and the word "take" meant both.
    /// </para>
    /// <para>
    /// <b>#458 — the arm clears its own path.</b> Arming an injected source
    /// needed the radio's transmit input set to this computer, and that picker
    /// lives on a different category, so a single operator task was split
    /// across a tab change. A screen-reader operator who navigates away from a
    /// control is holding it in memory from that moment.
    /// </para>
    /// <para>
    /// <b>Source rules rather than behaviour tests, and deliberately so.</b>
    /// Both defects live in a WPF dialog that this suite must never construct,
    /// and neither is expressible as a compiled shape — one is "these two code
    /// paths consult the same store", the other is "every road out of an armed
    /// state puts the radio back". A comment asking for either would be
    /// obeyed until the first editor who did not read it.
    /// </para>
    /// </remarks>
    public sealed class TakePlaybackAndInjectionPathTests
    {
        private static string Workshop(string suffix) => Path.Combine(
            IntegrationPassTree.Root, "JJFlexWpf", "Dialogs",
            "AudioWorkshopDialog." + suffix + ".cs");

        private static string ReadWorkshop(string suffix)
        {
            string path = Workshop(suffix);
            Assert.True(File.Exists(path),
                "Expected " + path + ". The Audio Workshop's partial files have been renamed or "
                + "moved, so every rule in this class is scanning nothing — which reads exactly "
                + "like a pass. Repoint it before believing a green result.");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// The body of a method, from its signature to the matching close
        /// brace. Crude by design: these files are ordinary formatted C# and
        /// the alternative is a Roslyn parse for a brace count.
        /// </summary>
        private static string MethodBody(string source, string signatureFragment)
        {
            int at = source.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(at >= 0,
                "Could not find \"" + signatureFragment + "\". It has been renamed or removed, so "
                + "the rule built on it is proving nothing.");
            int open = source.IndexOf('{', at);
            Assert.True(open > 0, "No body found for \"" + signatureFragment + "\".");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(open, i - open + 1);
                }
            }
            throw new InvalidOperationException("Unbalanced braces after \"" + signatureFragment + "\".");
        }

        // ── #455 ────────────────────────────────────────────────────────────

        /// <summary>
        /// Play last take must consult the recordings folder as well as the
        /// radio, and must not say there is nothing until it has asked both.
        /// </summary>
        [Fact]
        public void PlayLastTakeAsksBothStoresBeforeSayingThereIsNoTake()
        {
            string body = MethodBody(ReadWorkshop("AudioCheck"), "private void PlayLastTake()");

            int radio = body.IndexOf("SlicePlayEnabled", StringComparison.Ordinal);
            int folder = body.IndexOf("RecordingStore.Newest", StringComparison.Ordinal);
            int denial = body.IndexOf("audio.check.no_recording_yet", StringComparison.Ordinal);

            Assert.True(radio >= 0,
                "Play last take no longer asks the radio's record buffer, so a take made by a "
                + "Record and play back check has become unreachable from the button that offers "
                + "to play it. See task #455.");
            Assert.True(folder >= 0,
                "Play last take no longer asks RecordingStore, which is the store Open recordings "
                + "folder opens and the store the recorder writes to. That is exactly the split "
                + "that made this button tell Don he had no recordings while his files sat in the "
                + "folder. See task #455.");
            Assert.True(denial > radio && denial > folder,
                "Play last take announces that there is no take before it has finished asking both "
                + "stores. The message is only honest after both have said no. See task #455.");
        }

        /// <summary>
        /// A take that exists on this computer must be playable on this
        /// computer. Nothing else in the tree can play a recording back.
        /// </summary>
        [Fact]
        public void ThereIsExactlyOnePlaceARecordingIsPlayedBack()
        {
            string player = Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "RecordingPlayer.cs");
            Assert.True(File.Exists(player),
                "RecordingPlayer is gone. Without it the application can write a recording and "
                + "then has no way at all to play it, which is half of what task #455 was: the "
                + "only button offering to play a take could not reach the files.");

            string body = MethodBody(ReadWorkshop("AudioCheck"), "private void PlayLastTake()");
            Assert.Contains("RecordingPlayer", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// Playing a take must not require a radio. The folder it comes from
        /// opens without one and the files play in other programs without one.
        /// </summary>
        [Fact]
        public void PlayLastTakeDoesNotRefuseWhenNoRadioIsConnected()
        {
            string body = MethodBody(ReadWorkshop("AudioCheck"), "private void PlayLastTake()");
            Assert.DoesNotContain("audio.no_radio_connected", body, StringComparison.Ordinal);
        }

        // ── #458 ────────────────────────────────────────────────────────────

        /// <summary>
        /// Both arms go through the one helper, so whatever it learns to clear,
        /// both inherit — and neither grows a second opinion about the same
        /// three preconditions.
        /// </summary>
        [Fact]
        public void BothInjectedSourcesArmThroughTheSamePathHelper()
        {
            Assert.Contains("WithInjectionPath",
                MethodBody(ReadWorkshop("TestTone"), "private void ToneArmChanged(bool armed)"),
                StringComparison.Ordinal);
            Assert.Contains("WithInjectionPath",
                MethodBody(ReadWorkshop("ReferenceAudio"), "private void ReferenceArmChanged(bool armed)"),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Every road out of an armed state puts the transmit input back.
        /// </summary>
        /// <remarks>
        /// There are more of these than there look to be, which is the reason
        /// for a test rather than care: the operator unticking the box, the
        /// recording playing itself out, the Ctrl+J leader chord releasing the
        /// tone from outside this dialog, the dialog closing, and the radio
        /// going away. A restore that covers four of the five leaves an
        /// operator transmitting from a microphone they did not choose.
        /// </remarks>
        [Theory]
        [InlineData("TestTone", "private void DisarmTone(bool speak, FlexBase? rig = null)")]
        [InlineData("TestTone", "private void SyncToneArmUi()")]
        [InlineData("ReferenceAudio", "private void DisarmReference(bool speak, FlexBase? rig = null)")]
        [InlineData("ReferenceAudio", "private void SyncReferenceUi()")]
        public void EveryReleasePathRestoresTheTransmitInput(string file, string signature)
        {
            Assert.Contains("RestoreInjectionPath",
                MethodBody(ReadWorkshop(file), signature), StringComparison.Ordinal);
        }

        /// <summary>
        /// The helper decides what it may change by asking the radio's own
        /// state, never by reading the words in the refusal.
        /// </summary>
        /// <remarks>
        /// <c>TxTonePathTrouble</c> returns operator-facing prose, and this
        /// application's strings are editable by the operator and destined for
        /// translation. Branching on that text would make a reword silently
        /// change what the dialog does to a transmitter. The rule is cheap to
        /// state and the mistake is very easy to make.
        /// </remarks>
        [Fact]
        public void ThePathHelperNeverBranchesOnTheRefusalWording()
        {
            string path = Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs",
                "AudioWorkshopDialog.InjectionPath.cs");
            Assert.True(File.Exists(path),
                "The injection-path helper is gone, so the two arms have no shared answer about "
                + "the transmit path and task #458's tab trip is free to come back.");

            string body = MethodBody(File.ReadAllText(path),
                "private static bool CanSwitchTransmitInput(FlexBase rig, out string pcOption)");

            Assert.Contains("rig.PCAudio", body, StringComparison.Ordinal);
            Assert.Contains("rig.Mode", body, StringComparison.Ordinal);
            Assert.Contains("MicSourceList", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TxTonePathTrouble", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// PC audio is NOT switched on from here, and that is a decision.
        /// </summary>
        /// <remarks>
        /// Turning PC audio on runs a device check that can put a picker on
        /// screen and carries a remembered per-radio choice; both belong to the
        /// road that owns them, in Settings. An arm that quietly took that
        /// decision would be reaching a long way outside what an operator
        /// ticking "test tone" asked for.
        /// </remarks>
        [Fact]
        public void ArmingDoesNotTurnPcAudioOnByItself()
        {
            string path = Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs",
                "AudioWorkshopDialog.InjectionPath.cs");
            Assert.DoesNotContain("PCAudio = true", File.ReadAllText(path), StringComparison.Ordinal);
        }

        /// <summary>
        /// The rules above, shown to be able to go red. Every one of them is a
        /// "contains" over source, and a matcher that has stopped matching
        /// reports a clean tree it never looked at.
        /// </summary>
        [Fact]
        public void TheMethodExtractorFindsABodyAndRefusesAMissingOne()
        {
            const string sample = @"
class C
{
    private void PlayLastTake()
    {
        if (Something()) { Nested(); }
        Done();
    }
    private void After() { }
}";
            string body = MethodBody(sample, "private void PlayLastTake()");
            Assert.Contains("Nested();", body, StringComparison.Ordinal);
            Assert.DoesNotContain("After", body, StringComparison.Ordinal);

            Assert.ThrowsAny<Exception>(() => MethodBody(sample, "private void NoSuchMethod()"));
        }
    }
}
