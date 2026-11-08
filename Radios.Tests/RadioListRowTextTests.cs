#nullable enable

using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The radio list (#1008, measured 2026-11-08): a row's printed text and
    /// its accessible name disagreed ("on the local network, online" printed,
    /// "on SmartLink, online" spoken), and the list's "{count} listed, {live}
    /// online" name kept an old count. The printed text was ToString() made
    /// once with the row's container; the refresh's change test compared each
    /// row with itself, so an in-place change never reached the screen or the
    /// list's name. Read from source: the dialog is a WPF window.
    /// </summary>
    public sealed class RadioListRowTextTests
    {
        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), Path.Combine(parts)));

        [Fact]
        public void The_printed_text_and_the_accessible_name_are_bound_to_one_source()
        {
            string xaml = Read("JJFlexWpf", "Dialogs", "RigSelectorDialog.xaml");
            Assert.Contains("<TextBlock Text=\"{Binding DisplayText, Mode=OneWay}\"/>", xaml);
            Assert.Contains("<Setter Property=\"AutomationProperties.Name\" Value=\"{Binding DisplayText, Mode=OneWay}\"/>", xaml);
        }

        [Fact]
        public void Every_refresh_tells_each_row_to_re_read_its_text_and_renames_the_list_even_when_no_row_moved()
        {
            string src = Read("JJFlexWpf", "Dialogs", "RigSelectorDialog.xaml.cs");
            Assert.Contains("public class RadioListItem : Radios.IRosterOrderKey, INotifyPropertyChanged", src);
            int at = src.IndexOf("private void RefreshRadiosList(bool reorder = false)", StringComparison.Ordinal);
            Assert.True(at >= 0);
            int notify = src.IndexOf("foreach (var row in rows) row.NotifyDisplayTextChanged();", at, StringComparison.Ordinal);
            int compare = src.IndexOf("bool identical = true;", at, StringComparison.Ordinal);
            int rename = src.IndexOf("UpdateListAutomationName();\r\n                return;", at, StringComparison.Ordinal);
            if (rename < 0) rename = src.IndexOf("UpdateListAutomationName();\n                return;", at, StringComparison.Ordinal);
            int clear = src.IndexOf("RadiosBox.Items.Clear();", at, StringComparison.Ordinal);
            Assert.True(notify > at && compare > notify, "the rows must re-read their text before the change test");
            Assert.True(rename > compare && clear > rename, "the unchanged branch must rename the list before it returns");
        }

        [Fact]
        public void The_lists_count_follows_the_counted_list_rule()
        {
            string src = Read("JJFlexWpf", "Dialogs", "RigSelectorDialog.xaml.cs");
            int at = src.IndexOf("private void UpdateListAutomationName()", StringComparison.Ordinal);
            Assert.True(at >= 0);
            string body = src.Substring(at, src.IndexOf("private bool _listNamingOnFocus;", at, StringComparison.Ordinal) - at);
            Assert.Contains("ListNaming.RenameNow(RadiosBox.IsKeyboardFocused, _listNamingOnFocus, RadiosBox.Items.Count)", body);
            Assert.Contains("_listNameOwed = true;", body);
            int lost = src.IndexOf("private void RadiosBox_LostFocus(", StringComparison.Ordinal);
            Assert.Contains("_listNameOwed", src.Substring(lost, 600));
        }
    }
}
