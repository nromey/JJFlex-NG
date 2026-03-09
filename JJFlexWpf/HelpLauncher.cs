using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;  // For Help.ShowHelp

namespace JJFlexWpf
{
    public static class HelpLauncher
    {
        private static string _helpFilePath;

        private static readonly Dictionary<string, string> ContextMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "FreqDisplay", "pages/tuning-frequency.htm" },
            { "ScreenFieldsDSP", "pages/filters-dsp.htm" },
            { "ScreenFieldsTX", "pages/ptt-transmission.htm" },
            { "ScreenFieldsAudio", "pages/audio-workshop.htm" },
            { "ScreenFieldsReceiver", "pages/filters-dsp.htm" },
            { "ScreenFieldsAntenna", "pages/filters-dsp.htm" },
            { "AudioWorkshop", "pages/audio-workshop.htm" },
            { "EarconExplorer", "pages/earcon-explorer.htm" },
            { "MeterTones", "pages/meter-tones.htm" },
            { "LogPanel", "pages/logging.htm" },
            { "SettingsDialog", "pages/settings-profiles.htm" },
            { "CommandFinder", "pages/keyboard-reference.htm" },
            { "LeaderKey", "pages/leader-key.htm" },
            { "WelcomeDialog", "pages/getting-started.htm" },
            { "ConnectDialog", "pages/connection-troubleshooting.htm" },
        };

        public static void Initialize()
        {
            _helpFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "JJFlexRadio.chm");
        }

        public static void ShowHelp(string context = null)
        {
            if (string.IsNullOrEmpty(_helpFilePath) || !File.Exists(_helpFilePath))
            {
                Radios.ScreenReaderOutput.Speak("Help file not found.");
                return;
            }

            string topic = null;
            if (context != null && ContextMap.TryGetValue(context, out var page))
                topic = page;

            try
            {
                if (topic != null)
                    System.Windows.Forms.Help.ShowHelp(null, _helpFilePath, HelpNavigator.Topic, topic);
                else
                    System.Windows.Forms.Help.ShowHelp(null, _helpFilePath);
            }
            catch (Exception ex)
            {
                Radios.ScreenReaderOutput.Speak("Could not open help file.");
                System.Diagnostics.Trace.WriteLine($"HelpLauncher error: {ex.Message}");
            }
        }
    }
}
