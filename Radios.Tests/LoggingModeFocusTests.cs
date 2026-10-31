using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #849: the first entry to Logging mode lands on the Call Sign field. A
    /// control that has never been laid out cannot take focus, and this needs a
    /// real window to show, so the order is pinned here and the harness reads
    /// the focused element by UI Automation.
    /// </summary>
    public sealed class LoggingModeFocusTests
    {
        private static string Read(string relative)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
            Assert.True(d != null, "could not find the repository root");
            return File.ReadAllText(Path.Combine(d!.FullName, relative));
        }

        private static string Control => Read("JJFlexWpf/LogEntryControl.xaml.cs");

        [Fact]
        public void The_field_is_laid_out_before_focus_is_asked_for_the_second_time()
        {
            string c = Control;
            int method = c.IndexOf("public void FocusCallSign()", StringComparison.Ordinal);
            Assert.True(method >= 0);
            int first = c.IndexOf("if (TakeCallSignFocus()) return;", method, StringComparison.Ordinal);
            int layout = c.IndexOf("UpdateLayout();", method, StringComparison.Ordinal);
            int second = c.IndexOf("if (TakeCallSignFocus()) return;", first + 1, StringComparison.Ordinal);
            int late = c.IndexOf("Dispatcher.BeginInvoke(DispatcherPriority.Loaded", method, StringComparison.Ordinal);

            Assert.True(first > 0 && first < layout && layout < second && second < late,
                "try, lay out, try, then ask again once the panel has shown");
        }

        [Fact]
        public void Success_is_read_from_the_field_and_not_from_having_asked()
        {
            Assert.Contains("return CallSignBox.IsKeyboardFocused;", Control, StringComparison.Ordinal);
        }

        [Fact]
        public void The_field_keeps_its_real_name()
        {
            string xaml = Read("JJFlexWpf/LogEntryControl.xaml");
            int box = xaml.IndexOf("x:Name=\"CallSignBox\"", StringComparison.Ordinal);
            Assert.True(box >= 0);
            Assert.Contains("AutomationProperties.Name=\"Call Sign\"", xaml.Substring(box, 120), StringComparison.Ordinal);
        }
    }
}
