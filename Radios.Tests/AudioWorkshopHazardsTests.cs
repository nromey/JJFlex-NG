using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #480's two live hazards: the Audio Workshop's Reset now asks before it
    /// writes the radio's live transmit chain, as Delete Preset already did,
    /// and the Earcon Explorer's last bare Slider is a ValueFieldControl like
    /// every other number in the window.
    /// </summary>
    /// <remarks>From source: both are inside a window. Pressing them is the harness's.</remarks>
    public sealed class AudioWorkshopHazardsTests
    {
        private static string Read(string file) =>
            File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", file)).Replace("\r\n", "\n");

        [Fact]
        public void Reset_asks_before_it_writes_the_live_transmit_chain()
        {
            string src = Read("AudioWorkshopDialog.Toolbar.cs");
            int at = src.IndexOf("private void Reset_Click(object sender, RoutedEventArgs e)", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = src.Substring(at, src.IndexOf("\n    }\n", at, StringComparison.Ordinal) - at);
            int ask = body.IndexOf("new ConfirmActionDialog(", StringComparison.Ordinal);
            int refused = body.IndexOf("if (confirm.ShowDialog() != true)", StringComparison.Ordinal);
            int write = body.IndexOf(".ApplyTo(LiveRig)", StringComparison.Ordinal);
            Assert.True(ask > 0 && ask < refused && refused < write,
                "Reset must confirm, and return on a No, before it applies the default preset to the radio");
            Assert.Contains("settings.audio.reset_tx_audio_title", body, StringComparison.Ordinal);
            Assert.Contains("settings.audio.reset_tx_audio_body", body, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Earcon_Explorer_has_no_bare_slider_left()
        {
            string src = Read("AudioWorkshopDialog.Earcons.cs");
            // Positive control: the bench numbers are still built here.
            Assert.Contains("AddEarconBenchValue(panel, \"Bench level\"", src, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"\bnew Slider\b"), src);
            Assert.DoesNotMatch(new Regex(@"\bSlider\?\s+_earconBench"), src);
            Assert.Contains("private ValueFieldControl? _earconBenchGap;", src, StringComparison.Ordinal);
        }
    }
}
