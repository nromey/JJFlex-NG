using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #848: after a session ends, the title says no radio, in the words a
    /// fresh launch uses. The routes themselves need the running app, so they
    /// are pinned from source here and read by UI Automation on the harness.
    /// </summary>
    public sealed class MainWindowTitleTests
    {
        private static string Read(string relative)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
            Assert.True(d != null, "could not find the repository root");
            return File.ReadAllText(Path.Combine(d!.FullName, relative));
        }

        /// <summary>A method's text, from its signature to the next signature-like line.</summary>
        private static string Between(string text, string from, string to)
        {
            int a = text.IndexOf(from, StringComparison.Ordinal);
            Assert.True(a >= 0, from);
            int b = text.IndexOf(to, a, StringComparison.Ordinal);
            Assert.True(b > a, to);
            return text.Substring(a, b - a);
        }

        [Fact]
        public void The_line_reads_as_a_fresh_launch_reads()
        {
            Assert.Equal("JJ Flexible Radio Access station — no radio connected",
                         MainWindowTitle.NoRadio("station", 1));
        }

        [Fact]
        public void A_second_copy_carries_its_number_as_the_station_name_does()
        {
            Assert.Equal("JJ Flexible Radio Access station2 — no radio connected",
                         MainWindowTitle.NoRadio("station", 2));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("  ")]
        public void With_no_station_the_title_still_reads_whole(string? station)
        {
            Assert.Equal("JJ Flexible Radio Access — no radio connected", MainWindowTitle.NoRadio(station!, 2));
        }

        [Fact]
        public void A_fresh_launch_takes_the_line_from_the_one_source()
        {
            string globals = Read("globals.vb");
            Assert.DoesNotContain("AppShellForm.Text &= \" — no radio connected\"", globals, StringComparison.Ordinal);
            Assert.Contains("AppShellForm.Text = NoRadioWindowTitle()", globals, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_end_of_a_session_puts_the_line_up()
        {
            string globals = Read("globals.vb");
            string close = Between(globals, "Friend Sub CloseTheRadio()", "End Sub");
            Assert.Contains("ShowNoRadioTitle()", close, StringComparison.Ordinal);

            string main = Read("JJFlexWpf/MainWindow.xaml.cs");
            string off = Between(main, "private void PowerNowOffInternal()", "    /// <summary>");
            Assert.Contains("bool hadSession = _radioPowerOn;", off, StringComparison.Ordinal);
            Assert.Contains("if (hadSession) ShowNoRadioTitle();", off, StringComparison.Ordinal);

            string rescue = Between(main, "public void EnterRescueMode(string lead)", "ApplyRescueVisibility();");
            Assert.Contains("ShowNoRadioTitle();", rescue, StringComparison.Ordinal);
            Assert.DoesNotContain("no radio connected\"", rescue, StringComparison.Ordinal);
        }

        [Fact]
        public void The_window_is_only_written_when_the_title_has_changed()
        {
            // A title write can make the screen reader speak the window (#550).
            string globals = Read("globals.vb");
            string wiring = Between(globals, "WpfMainWindow.UpdateTitleBar = Sub(title)", "End Sub");
            Assert.Contains("AppShellForm.Text <> title", wiring, StringComparison.Ordinal);
        }

        [Fact]
        public void An_exit_does_not_change_the_title_on_its_way_out()
        {
            string globals = Read("globals.vb");
            string title = Between(globals, "Friend Function NoRadioWindowTitle()", "End Function");
            Assert.DoesNotContain("Ending", title, StringComparison.Ordinal); // the launch line needs it whatever
            string wiring = Between(globals, "WpfMainWindow.NoRadioTitleSource =", "WpfMainWindow.WireRadioEvents()");
            Assert.Contains("If(Ending, Nothing, NoRadioWindowTitle())", wiring, StringComparison.Ordinal);
        }
    }
}
