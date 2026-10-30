using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Repairs Claude made after Codex's round-1030 check of Check your audio
    /// (#835, 2026-10-30). Source pins, because the dialog cannot be built here.
    /// </summary>
    public sealed class CheckYourAudioRound1030RepairTests
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

        /// <summary>
        /// B6: "Each check counts down unless that function is turned off"
        /// (Noel, 2026-10-29 23:59). The computer microphone check keys nothing
        /// but is a check: it takes the same start admission, counts down, and
        /// opens the microphone only once the count lets it.
        /// </summary>
        [Fact]
        public void The_computer_microphone_check_counts_down_before_it_listens()
        {
            string src = Source("JJFlexWpf/Dialogs/CheckYourAudioDialog.ComputerMic.cs");
            string toggle = Body(src, "private void ToggleComputerMicCheck()");
            Assert.Contains("StartComputerMicCheckAsync()", toggle);
            Assert.DoesNotContain("probe.Start(", toggle);

            string start = Body(src, "private async Task StartComputerMicCheckAsync()");
            int admit = start.IndexOf("_checkStarts.TryBegin()", StringComparison.Ordinal);
            int count = start.IndexOf("BeginStartCountdown(AudioCheckListenMethods.ComputerMic)", StringComparison.Ordinal);
            int wait = start.IndexOf("await CountdownLetsKeyAsync()", StringComparison.Ordinal);
            int open = start.IndexOf("StartComputerMicProbeNow()", StringComparison.Ordinal);
            Assert.True(admit >= 0 && count > admit && wait > count && open > wait,
                "the computer check must take admission, count, wait for the count, and only then open the microphone");
            Assert.Contains("!start.IsCurrent", start);
            Assert.Contains("EndStartCountdown()", start);

            // A count is planned for it like any other check, and only the switch removes it.
            Assert.NotNull(CheckCountdown.PlanFor(AudioCheckListenMethods.ComputerMic, new CountdownSettings(), true, VerbosityLevel.Chatty));
            Assert.Null(CheckCountdown.PlanFor(AudioCheckListenMethods.ComputerMic, new CountdownSettings { Enabled = false }, true, VerbosityLevel.Chatty));
        }

        /// <summary>
        /// #856, found 2026-10-30: with no radio connected, Settings OK saves
        /// only a list of the operator's own fields to the root audio file.
        /// The countdown is one of them, so it is on the list; otherwise a
        /// countdown set while disconnected applied and was then lost.
        /// </summary>
        [Fact]
        public void The_countdown_is_saved_by_Settings_with_no_radio_connected()
        {
            // Sound timing (#855) moved the user-scope list out of the menu bar
            // into AudioOutputConfig.PersistUserScope, which the menu bar calls.
            Assert.Contains("AudioOutputConfig.PersistUserScope(audioConfig, rootConfigDir);", Source("JJFlexWpf/NativeMenuBar.cs"));
            string src = Source("JJFlexWpf/AudioOutputConfig.cs");
            int list = src.IndexOf("var rootConfig = Load(rootConfigDir);", StringComparison.Ordinal);
            int countdown = src.IndexOf("rootConfig.Countdown = chosen.Countdown.Clone();", list, StringComparison.Ordinal);
            int save = src.IndexOf("rootConfig.Save(rootConfigDir);", list, StringComparison.Ordinal);
            Assert.True(list >= 0 && countdown > list && save > countdown,
                "the countdown must be copied into the root audio file before the disconnected save");
        }

        /// <summary>
        /// #856's cause: on a launch with no radio, the settings folder came
        /// from FreqHandlers, which exists only after power-on, so Settings
        /// opened on defaults and never saved. The settings root is the
        /// fallback; and the saved countdown is applied at startup, because
        /// the computer microphone check counts down with no radio at all.
        /// </summary>
        [Fact]
        public void Settings_with_no_radio_reads_and_saves_the_settings_root_and_startup_applies_the_countdown()
        {
            string bar = Source("JJFlexWpf/NativeMenuBar.cs");
            Assert.Contains("string? rootConfigDir = handlers?.GetConfigDirectory?.Invoke() ?? Radios.RadioConfig.AppDataRoot;", bar);
            string main = Source("JJFlexWpf/MainWindow.xaml.cs");
            int load = main.IndexOf("var userConfig = AudioOutputConfig.Load(baseConfigDir);", StringComparison.Ordinal);
            int countdown = main.IndexOf("Radios.CountdownSettings.Current = userConfig.Countdown;", StringComparison.Ordinal);
            Assert.True(load >= 0 && countdown > load, "startup must apply the saved countdown from the settings root");
        }
    }
}
