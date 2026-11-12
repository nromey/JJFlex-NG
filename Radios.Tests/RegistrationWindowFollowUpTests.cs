#nullable enable
using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Radio Setup's SmartLink registration, two follow-ups (#975 items 1 and
    /// 4), read from source: the window cannot be built here, and what is said
    /// after Settings closes is the harness's to hear.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RegistrationWindowFollowUpTests
    {
        /// <summary>
        /// Item 1: the unregister confirmation names the account the command
        /// is sent with, as the register confirmation does, and does not claim
        /// the radio is registered under it.
        /// </summary>
        [Fact]
        public void The_unregister_confirmation_names_the_account_it_acts_with()
        {
            string src = Source("JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs");
            string click = Between(src, "private void SetupUnregisterButton_Click(", "StartRegistration(register: false, check);");
            // The sentence and the account it is given, in one call.
            int key = click.IndexOf("\"settings.radio.unregister.confirm_body\",", StringComparison.Ordinal);
            int account = click.IndexOf("(\"accountEmail\", check.AccountEmail)", StringComparison.Ordinal);
            Assert.True(key > 0 && account > key && account - key < 120, "the unregister confirmation is given the account it acts with");

            Lexicon.Load(Lexicon.Partitions);
            string body = Lexicon.Get("settings.radio.unregister.confirm_body", ("accountEmail", "nobody@example.com"));
            Assert.Contains("nobody@example.com", body, StringComparison.Ordinal);
            Assert.DoesNotContain("registered to", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("registered under", body, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Item 4: once Settings has closed, a registration still running on
        /// the radio writes no control and runs no refresh, and is still
        /// spoken: the prompt to key the microphone and the verdict.
        /// </summary>
        [Fact]
        public void After_the_window_closes_progress_writes_nothing_and_is_still_spoken()
        {
            string src = Source("JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs");
            Assert.Contains("Closed += (_, _) => _radioSetupClosed = true;", src, StringComparison.Ordinal);

            string onState = Between(src, "void OnState(string text, bool terminal) => Dispatcher.Invoke(() =>", "\n            });");
            Assert.Contains("bool closed = _radioSetupClosed;", onState, StringComparison.Ordinal);
            Assert.Contains("if (!closed) SetupRegisterStatus.Text = text;", onState, StringComparison.Ordinal);
            Assert.Contains("if (terminal && !closed)", onState, StringComparison.Ordinal);
            // The only unguarded line that reaches out is the speech.
            int speak = onState.IndexOf("ScreenReaderOutput.Speak(", StringComparison.Ordinal);
            Assert.True(speak > 0, "the progress is still spoken");
            Assert.DoesNotContain("closed", onState.Substring(speak, onState.IndexOf(";", speak, StringComparison.Ordinal) - speak),
                StringComparison.Ordinal);
            // Every write to a control in the callback is behind the guard.
            foreach (string line in onState.Split('\n').Where(l => l.Contains(".Text =", StringComparison.Ordinal)))
                Assert.Contains("if (!closed)", line, StringComparison.Ordinal);
        }

        private static string Between(string text, string start, string end)
        {
            int at = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(at >= 0, "not found: " + start);
            int stop = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
            Assert.True(stop > at, "no end after: " + start);
            return text.Substring(at, stop - at);
        }

        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        }
    }
}
