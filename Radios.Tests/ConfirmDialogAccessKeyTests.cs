using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1097: in the question box with a "don't show this again" box, no two
    /// controls share an access key. The station-save question's box and its
    /// "Don't save" button were both Alt+D, so Alt+D only moved focus between
    /// them and never pressed Don't save. Read from source: the dialog is a
    /// window, and a window is not built on a live desk.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ConfirmDialogAccessKeyTests
    {
        private static string Xaml() => File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(),
            "JJFlexWpf", "Dialogs", "ConfirmActionDialog.xaml"));

        /// <summary>The access key WPF takes from a label: the letter after the first single underscore.</summary>
        internal static char? AccessKey(string label)
        {
            for (int i = 0; i < label.Length - 1; i++)
            {
                if (label[i] != '_') continue;
                if (label[i + 1] == '_') { i++; continue; }
                return char.ToUpperInvariant(label[i + 1]);
            }
            return null;
        }

        private static string Content(string xaml, string xName)
        {
            var m = Regex.Match(xaml, "<\\w+\\s+x:Name=\"" + Regex.Escape(xName) + "\"[^>]*?Content=\"([^\"]*)\"", RegexOptions.Singleline);
            Assert.True(m.Success, xName + " has no literal Content in ConfirmActionDialog.xaml");
            return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        }

        /// <summary>
        /// Every caller that shows the box with its own Yes and No labels
        /// (ConfirmActionDialog with a suppression key): the box, Yes and No
        /// each have their own key. The jacks button is left out: it shows
        /// only for a radio model, and no suppression caller passes one.
        /// </summary>
        [Theory]
        [InlineData("settings.station_layout.menu_yes_label", "settings.station_layout.menu_no_label")]
        [InlineData("settings.profile.cascade_on_confirm_yes", "settings.profile.cascade_on_confirm_no")]
        public void The_dont_show_box_and_the_two_answers_have_three_different_access_keys(string yesKey, string noKey)
        {
            string xaml = Xaml();
            var labels = new Dictionary<string, string>
            {
                ["the don't-show box"] = Content(xaml, "DontShowAgainCheck"),
                ["Yes"] = Lexicon.Get(yesKey),
                ["No"] = Lexicon.Get(noKey),
            };
            var keys = labels.ToDictionary(l => l.Key, l => AccessKey(l.Value));
            Assert.All(keys, k => Assert.True(k.Value.HasValue, k.Key + " (\"" + labels[k.Key] + "\") has no access key"));
            var clashes = keys.GroupBy(k => k.Value).Where(g => g.Count() > 1)
                .Select(g => "Alt+" + g.Key + ": " + string.Join(" and ", g.Select(k => k.Key + " (\"" + labels[k.Key] + "\")")))
                .ToList();
            Assert.True(clashes.Count == 0, "Shared access keys: " + string.Join("; ", clashes));
        }

        /// <summary>The station-save call passes those two labels and the suppression key (so the theory covers the real caller).</summary>
        [Fact]
        public void The_station_save_question_is_the_caller_the_theory_reads()
        {
            string menu = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "NativeMenuBar.cs"));
            int at = menu.IndexOf("private static bool ConfirmStationSave(", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = menu.Substring(at, menu.IndexOf("return confirm.ShowDialog() == true;", at, StringComparison.Ordinal) - at);
            Assert.Contains("\"settings.station_layout.menu_yes_label\"", body);
            Assert.Contains("\"settings.station_layout.menu_no_label\"", body);
            Assert.Contains("suppressKey: Radios.AdvisoryKeys.StationSaveExplanation", body);
        }

        [Fact]
        public void The_access_key_reader_reads_what_WPF_reads()
        {
            Assert.Equal('D', AccessKey("_Don't save"));
            Assert.Equal('E', AccessKey("Don't show this _explanation again"));
            Assert.Equal('B', AccessKey("A__b _bc"));
            Assert.Null(AccessKey("No key here"));
        }
    }
}
