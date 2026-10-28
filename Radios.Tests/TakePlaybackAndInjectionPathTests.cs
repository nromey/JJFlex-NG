using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Two rules about Check your audio (the Audio Workshop's Hear Yourself
    /// category until #835), both of them about a control describing something
    /// other than what it does.
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
        // The partials moved with the page into Check your audio in #835: the
        // check is CheckYourAudioDialog.Check.cs, and the take-playback rules
        // both windows share are ListenbackTakes.cs.
        private static string Workshop(string suffix) => Path.Combine(
            IntegrationPassTree.Root, "JJFlexWpf", "Dialogs",
            suffix switch
            {
                "AudioCheck" => "CheckYourAudioDialog.Check.cs",
                "Listenback" => "ListenbackTakes.cs",
                "OffAir" => "CheckYourAudioDialog.OffAir.cs",
                _ => "CheckYourAudioDialog." + suffix + ".cs",
            });

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
        /// Monitor offers no play button; each take-making method answers
        /// only for its own kind and never silently substitutes one.
        /// </summary>
        [Fact]
        public void PlayLastTakeFollowsTheMethodAndMonitorHidesTheButton()
        {
            string body = MethodBody(ReadWorkshop("AudioCheck"), "private void PlayLastTake()");
            string selection = MethodBody(ReadWorkshop("AudioCheck"),
                "private (AudioCheckListenMethods Method, TakePlaybackChoice Choice,");
            string radioChoice = MethodBody(ReadWorkshop("AudioCheck"),
                "private (SliceRecorderTarget? Target, DateTime? MadeUtc) NewestRadioTake()");
            Assert.Contains("SliceHasRecording", radioChoice, StringComparison.Ordinal);
            Assert.Contains("SelectTakeForPlayback()", body, StringComparison.Ordinal);
            // #835: "Monitor" became "every method that makes no take" (Test
            // your microphone, Monitor, Loopback), asked of one rule.
            Assert.Contains("if (!AudioCheckMethods.MakesTake(method))", selection, StringComparison.Ordinal);
            Assert.Contains("NewestRadioTake()", selection, StringComparison.Ordinal);
            Assert.Contains("ListenbackTakes.NewestDirectory()", selection, StringComparison.Ordinal);
            Assert.DoesNotContain("RecordingStore.Newest", selection, StringComparison.Ordinal);
            Assert.Contains("TakePlaybackSelector.Choose(method", selection, StringComparison.Ordinal);
            string label = MethodBody(ReadWorkshop("AudioCheck"), "private void SyncPlayTakeLabel()");
            Assert.Contains("Visibility.Collapsed", label, StringComparison.Ordinal);
            // Collapsed is out of the tab order; since #835 the button is also
            // hidden until a take exists to play.
            Assert.Contains("AudioCheckMethods.MakesTake(SelectedMethod)", label, StringComparison.Ordinal);
            Assert.Contains("selected.Choice != TakePlaybackChoice.None", label, StringComparison.Ordinal);
            Assert.Contains("selected.Method == AudioCheckListenMethods.OffAir", body, StringComparison.Ordinal);
            Assert.Contains("selected.Method == AudioCheckListenMethods.RecordPlayback", body, StringComparison.Ordinal);
            Assert.Contains("SayNoTake(\"off-air\")", body, StringComparison.Ordinal);
            Assert.Contains("SayNoTake(\"slice\")", body, StringComparison.Ordinal);
        }

        [Fact]
        public void PlaybackStartsOnlyAfterItsClickAndStopKeepsItsOwnCue()
        {
            string check = ReadWorkshop("AudioCheck");
            string manual = MethodBody(check, "private async Task PlayRadioTakeAsync(");
            string automatic = MethodBody(check, "private void TickPostUnkey(FlexBase rig)");
            string iq = MethodBody(ReadWorkshop("Listenback"),
                "internal static async Task<bool> PlayAfterClickAsync(");
            string controls = File.ReadAllText(Path.Combine(IntegrationPassTree.Root,
                "JJFlexWpf", "RecorderControls.cs"));
            string slice = MethodBody(controls, "public static async Task<RecorderOutcome> ToggleSlicePlayAsync(");
            string computer = MethodBody(controls, "public static async Task<RecorderOutcome> ToggleComputerPlayAsync(");
            string speak = MethodBody(controls, "public static void Speak(RecorderOutcome outcome)");

            AssertClickBefore(manual, "rig.StartSlicePlayAsync(target)");
            AssertClickBefore(automatic, "rig.StartSlicePlayAsync(_recorderTarget)");
            AssertClickBefore(slice, "rig.ToggleSlicePlayAsync(target, owner)");
            AssertClickBefore(computer, "ComputerRecorder.TogglePlaybackAsync");
            Assert.True(iq.IndexOf("EarconPlayer.ConfirmTone()", StringComparison.Ordinal) <
                iq.IndexOf("RecordingPlayer.Play(path", StringComparison.Ordinal));
            Assert.DoesNotContain("outcome.Cue == RecorderCue.PlayStart", speak, StringComparison.Ordinal);
            Assert.Contains("outcome.Cue == RecorderCue.PlayStop", speak, StringComparison.Ordinal);
        }

        private static void AssertClickBefore(string body, string start)
        {
            int click = body.IndexOf("PlayStartClick", StringComparison.Ordinal);
            int play = body.IndexOf(start, StringComparison.Ordinal);
            Assert.True(click >= 0 && play > click, "The start click must precede " + start + ".");
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

        [Fact]
        public void OldTakeDateLivesInTheButtonLabelAndPlaybackStartsWithoutSpeechWait()
        {
            string source = ReadWorkshop("AudioCheck");
            string label = MethodBody(source, "private void SyncPlayTakeLabel()");
            Assert.Contains("selected.MadeUtc", label, StringComparison.Ordinal);
            Assert.Contains("var selected = SelectTakeForPlayback();", label, StringComparison.Ordinal);
            Assert.Contains("AutomationProperties.SetName(_playTakeButton, label)", label, StringComparison.Ordinal);
            Assert.Contains("_playTakeButton.Content = label", label, StringComparison.Ordinal);
            Assert.Contains("_playTakeButton.GotFocus +=", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WaitForOldTakeDateAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WaitForOldTakeDateAsync", ReadWorkshop("Listenback"), StringComparison.Ordinal);
            Assert.DoesNotContain("WaitForOldTakeDateAsync", ReadWorkshop("OffAir"), StringComparison.Ordinal);
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
            string path = Workshop("InjectionPath");
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
            Assert.DoesNotContain("PCAudio = true", ReadWorkshop("InjectionPath"), StringComparison.Ordinal);
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
