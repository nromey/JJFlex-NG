using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The 2026-10-29 fix round's rulings for Check your audio's window (#835),
    /// pinned where they live: the computer-microphone method's new name, the
    /// method field's boundary sound on arrival, and the computer check's start
    /// and stop tones.
    /// </summary>
    /// <remarks>
    /// Source rules where the behaviour lives in a WPF window this suite must
    /// never construct (the reason <c>CheckYourAudioWiringTests</c> gives). The
    /// control's own arrival behaviour is driven, windowless, by
    /// <c>CycleFieldSequenceTests</c> in JJFlexWpf.Tests. What these cannot see:
    /// the sounds themselves, and what NVDA says. Each was shown red against the
    /// round's base, <c>356bf1f3</c>.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class CheckYourAudioFixRound1029Tests
    {
        private static string Source(string path)
        {
            string full = Path.Combine(IntegrationPassTree.Root, path);
            Assert.True(File.Exists(full), "Expected " + path + ". It has moved, so the rule reading it proves nothing.");
            return File.ReadAllText(full).Replace("\r\n", "\n");
        }

        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "Could not find \"" + signature + "\"; the rule built on it is proving nothing.");
            int open = source.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("Unbalanced braces after " + signature);
        }

        // ── Item 3: the computer-microphone method's name (ruled 00:45) ──

        [Fact]
        public void The_computer_microphone_method_is_called_what_Noel_chose_and_its_about_line_and_countdown_follow()
        {
            Assert.Equal("Is Windows hearing my microphone? No radio involved",
                Lexicon.Get("audio.check.method.computer_mic"));
            Assert.Contains("whether Windows is hearing your microphone", AudioCheckMethods.About(AudioCheckListenMethods.ComputerMic));
            Assert.Equal("the Windows microphone check", Lexicon.Get(CheckCountdown.ShortNameKey(AudioCheckListenMethods.ComputerMic)));
            Assert.Contains("the Windows microphone check", CheckCountdown.LeadIn(AudioCheckListenMethods.ComputerMic));
            string dialog = Source("JJFlexWpf/Dialogs/CheckYourAudioDialog.Check.cs");
            // Split so the lexicon coverage scan does not read this line as a key.
            string nameLine = "AudioCheckListenMethods.ComputerMic => " + "Lexicon" + ".Get(" + '"' + "audio.check.method.computer_mic" + '"' + ")";
            Assert.Contains(nameLine, dialog);
            // The other method keeps its name; the two no longer read alike.
            Assert.Equal("Test your microphone, nothing goes on the air", Lexicon.Get("audio.check.method.mic_test"));
            string help = Source("docs/help/md/AudioWorkshop/AudioWorkshop.md");
            Assert.Contains("#### Is Windows hearing my microphone? No radio involved {#check-audio-computer-mic}", help);
            Assert.DoesNotContain("Check this computer's microphone", help);
        }

        // ── Item 4: the boundary sound on ARRIVING at an end (ruled 01:17) ──

        [Fact]
        public void The_method_field_plays_the_boundary_sound_on_arriving_at_the_first_or_last_method()
        {
            string check = Source("JJFlexWpf/Dialogs/CheckYourAudioDialog.Check.cs");
            int boundary = check.IndexOf("_listenMethodControl.AtBoundary = atFirst => EarconPlayer.FilterBoundaryHitTone(isLowEdge: atFirst);", StringComparison.Ordinal);
            int arrival = check.IndexOf("_listenMethodControl.BoundaryOnArrival = true;", StringComparison.Ordinal);
            Assert.True(boundary >= 0 && arrival > boundary, "the method field sets BoundaryOnArrival beside its boundary sound");
            // Only that field: the control's default stays off for the other users.
            string control = Source("JJFlexWpf/Controls/CycleFieldControl.xaml.cs");
            Assert.Contains("public bool BoundaryOnArrival { get; set; }", control);
        }

        // ── Item 5: start and stop tones for the computer check (01:22) ──

        [Fact]
        public void The_computer_check_plays_the_recorders_start_tone_when_it_listens_and_its_stop_tone_when_it_ends()
        {
            string mic = Source("JJFlexWpf/Dialogs/CheckYourAudioDialog.ComputerMic.cs");
            string start = Body(mic, "private void ToggleComputerMicCheck()");
            int listening = start.IndexOf("_computerMicProbe = probe;", StringComparison.Ordinal);
            int tone = start.IndexOf("EarconPlayer.RecorderStartBeep();", StringComparison.Ordinal);
            int reset = start.IndexOf("listening.ResetLevels();", StringComparison.Ordinal);
            Assert.True(listening >= 0 && tone > listening, "the start tone plays once the microphone is listening, never on a failed start");
            Assert.True(reset > tone, "the levels start again after the tone, so the verdict is about the operator, not the tone");

            string stop = Body(mic, "private void StopComputerMicCheck(bool speak)");
            int gone = stop.IndexOf("if (probe == null) return;", StringComparison.Ordinal);
            int closed = stop.IndexOf("probe.Stop();", StringComparison.Ordinal);
            int stopTone = stop.IndexOf("EarconPlayer.RecorderStopBeep();", StringComparison.Ordinal);
            Assert.True(gone >= 0 && closed > gone && stopTone > closed,
                "the stop tone plays only when a check was listening, after the microphone is closed");
        }
    }
}
