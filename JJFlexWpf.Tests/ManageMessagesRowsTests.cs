using System.Threading;
using JJFlexWpf.Dialogs;
using Xunit;

namespace JJFlexWpf.Tests
{
    /// <summary>
    /// #717: the rows Manage Messages shows. An empty library shows one row
    /// that says so, instead of a list that reports nothing; a library with
    /// messages shows them exactly as given. Calls a static helper on the
    /// dialog type and constructs no window, so it is safe on a live desk.
    /// </summary>
    public class ManageMessagesRowsTests
    {
        [Fact]
        public void An_empty_library_shows_one_row_that_says_so()
        {
            AssertWindowless();
            Assert.Equal(new[] { "nothing here" }, CWMessageUpdateDialog.RowsFor(new string[0], "nothing here"));
            Assert.Equal(new[] { "nothing here" }, CWMessageUpdateDialog.RowsFor(null, "nothing here"));
        }

        [Fact]
        public void Messages_are_shown_as_given_with_their_key_and_kind()
        {
            AssertWindowless();
            var rows = new[] { "CQ, Ctrl+1, CW only", "73, Ctrl+2, CW and voice" };
            Assert.Same(rows, CWMessageUpdateDialog.RowsFor(rows, "nothing here"));
        }

        // WPF throws on constructing any element off an STA thread, so an MTA
        // test body cannot put a window on the operator's desktop (the
        // NoiseReductionPerChannelTests pattern).
        private static void AssertWindowless() =>
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
    }
}
