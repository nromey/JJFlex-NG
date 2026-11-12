using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #609, the ruled half only: one sentence inside the equalizer window
    /// explaining that adjustments can be tried before Finished (ruled worth doing
    /// 2026-09-24; the words are Codex's, 2026-11-11). A line in the
    /// window, first in it, and not a second spoken announcement racing the
    /// title the dialog speaks on arrival. Whether NVDA reads it on opening is
    /// a harness step, not something a source test can show.
    /// </summary>
    // In the settings statics collection because it loads the lexicon, which is process-wide.
    [Collection(RadioConfigStaticsCollection.Name)]
    public class EqualizerChangesLiveNoteTests
    {
        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), Path.Combine(parts)));

        [Fact]
        public void The_window_carries_the_line_first_as_plain_text()
        {
            string xaml = Read("JJFlexWpf", "Dialogs", "EqualizerDialog.xaml");
            int note = xaml.IndexOf("<TextBlock x:Name=\"ChangesLiveNote\"", StringComparison.Ordinal);
            int enable = xaml.IndexOf("x:Name=\"EnabledCheck\"", StringComparison.Ordinal);
            Assert.True(note > 0 && note < enable, "the line is not the first thing in the window");
            // DecorativeText is the text a screen reader is told to skip.
            Assert.DoesNotContain("DecorativeText x:Name=\"ChangesLiveNote\"", xaml);
        }

        [Fact]
        public void Its_words_come_from_the_lexicon_and_are_not_spoken_separately()
        {
            string code = Read("JJFlexWpf", "Dialogs", "EqualizerDialog.xaml.cs");
            const string key = "audio.eq.changes_live";
            // Split so the lexicon scanner does not read this test as a call site.
            Assert.Contains("ChangesLiveNote.Text = " + "Lexicon" + ".Get(" + Quoted(key) + ");", code);
            Assert.Equal(1, CountOf(code, key));   // set once, never handed to the speech layer as well
            Lexicon.Load(Lexicon.Partitions);
            Assert.False(string.IsNullOrWhiteSpace(Lexicon.Get(key)));
            Assert.NotEqual(key, Lexicon.Get(key));
        }

        private static string Quoted(string s) => "\"" + s + "\"";

        private static int CountOf(string text, string what)
        {
            int n = 0;
            for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
