using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The 2026-10-28 fix round's rulings for Check your audio and the Audio
    /// Workshop (#835), pinned where they live in source.
    /// </summary>
    /// <remarks>
    /// Source rules, for the reason <c>CheckYourAudioWiringTests</c> gives: each
    /// lives in a WPF window this suite must never construct. What they cannot
    /// see is whether a press does what the source says; every one has a bench
    /// step in the round's report. Each was shown red against the round's base,
    /// <c>4191ea3b</c>.
    /// </remarks>
    public sealed class CheckYourAudioFixRound1028Tests
    {
        private static string Source(string path)
        {
            string full = Path.Combine(IntegrationPassTree.Root, path);
            Assert.True(File.Exists(full), "Expected " + path + ". It has moved, so the rule reading it proves nothing.");
            return File.ReadAllText(full).Replace("\r\n", "\n");
        }

        private static string Dialog(string part) => Source("JJFlexWpf/Dialogs/" + part + ".cs");

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

        // ── Item 3: a microphone change, and which window opened the picker ──

        [Fact]
        public void A_save_in_Audio_Devices_tells_every_open_window_whichever_door_opened_it()
        {
            string picker = Dialog("AudioDevicesDialog.xaml");
            string ok = Body(picker, "private void OkButton_Click(object sender, RoutedEventArgs e)");
            int notice = ok.IndexOf("TransmitGainControls.MicrophoneChoiceMayHaveChanged(null);", StringComparison.Ordinal);
            int result = ok.IndexOf("DialogResult = true;", StringComparison.Ordinal);
            Assert.True(notice >= 0 && result > notice, "OK tells every binding, after the devices are saved and before it closes");
        }

        [Fact]
        public void The_trace_names_the_window_that_opened_the_picker_how_it_closed_and_which_window_rebound()
        {
            string picker = Dialog("AudioDevicesDialog.xaml");
            Assert.Contains("Audio Devices: opened from", Body(picker, "public static bool ShowPicker("));
            Assert.Contains("Audio Devices: closed with OK", picker);
            Assert.Contains("Audio Devices: closed WITHOUT saving", picker);

            string gain = Dialog("TransmitGainControls");
            Assert.Contains("\"Transmit gain (\" + Host + \"): binding the Windows level to", gain);
            Assert.Contains("host: \"Check your audio\"", Dialog("CheckYourAudioDialog"));
            Assert.Contains("host: \"Audio Workshop\"", Dialog("AudioWorkshopDialog.xaml"));
        }
    }
}
