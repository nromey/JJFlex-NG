using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #775's remainder: once a connect has started from the radio picker, the
    /// opening pass's "Local connection list loaded, still listening" (and its
    /// nothing-found sibling) is not spoken. Measured 2026-10-19: it landed
    /// 300 ms after the main window came up, over the connect, about a radio
    /// already found and chosen.
    /// </summary>
    /// <remarks>
    /// From source: the picker is a window. The harness re-captures a connect
    /// under NVDA and JAWS.
    /// </remarks>
    public sealed class PickerOpeningPassQuietTests
    {
        private static string Picker =>
            File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "RigSelectorDialog.xaml.cs"))
                .Replace("\r\n", "\n");

        [Fact]
        public void Every_connect_from_the_picker_marks_the_connect_started_first()
        {
            string src = Picker;
            int at = src.IndexOf("private void CompleteConnect(RadioListItem radio, ConnectPathKind path, bool forced,", StringComparison.Ordinal);
            Assert.True(at > 0, "CompleteConnect has moved");
            int open = src.IndexOf('{', at);
            string firstLine = src.Substring(open + 1, src.IndexOf('\n', open + 2) - open).Trim();
            Assert.StartsWith("_connectStarted = true;", firstLine, StringComparison.Ordinal);
        }

        [Fact]
        public void The_opening_pass_says_nothing_once_a_connect_has_started()
        {
            string src = Picker;
            int refresh = src.IndexOf("RefreshRadiosList(reorder: true);\n\n                // A connect already started", StringComparison.Ordinal);
            Assert.True(refresh > 0, "the opening pass's end has moved");
            int guard = src.IndexOf("if (!SaysOpeningPassLine(_connectStarted)) return;", refresh, StringComparison.Ordinal);
            int loaded = src.IndexOf("connect.selector.local_loaded_terse", refresh, StringComparison.Ordinal);
            int nothing = src.IndexOf("AnnounceNothingLive();", refresh, StringComparison.Ordinal);
            Assert.True(guard > 0 && guard < loaded && guard < nothing,
                "the connect-started guard must come before both of the opening pass's lines");
        }
    }
}
