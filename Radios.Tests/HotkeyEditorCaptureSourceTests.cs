using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #705: the Hotkey Editor's capture lets Tab and Shift+Tab through
    /// BEFORE it consumes the keystroke. The classification itself is held in
    /// JJFlexWpf.Tests (HotkeyEditorCaptureTests); this reads the handler,
    /// because the defect was an <c>e.Handled = true</c> at the top of it, and
    /// a pure classifier cannot see where that line sits.
    /// </summary>
    public sealed class HotkeyEditorCaptureSourceTests
    {
        [Fact]
        public void Tab_leaves_the_capture_before_anything_is_consumed()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf/Dialogs/KeysDialog.xaml.cs"));
            int handler = src.IndexOf("protected override void OnPreviewKeyDown(KeyEventArgs e)", StringComparison.Ordinal);
            Assert.True(handler >= 0, "KeysDialog.OnPreviewKeyDown was not found");

            int navigate = src.IndexOf("if (kind == CaptureKey.Navigate)", handler, StringComparison.Ordinal);
            int handled = src.IndexOf("e.Handled = true;", handler, StringComparison.Ordinal);
            int cancel = src.IndexOf("if (kind == CaptureKey.Cancel)", handler, StringComparison.Ordinal);
            Assert.True(navigate > handler, "the capture no longer asks whether the key navigates");
            Assert.True(handled > navigate, "the keystroke is consumed before Tab is let through");
            // Escape is still intercepted: CloseButton is IsCancel, so a
            // let-through Escape would close the dialog instead of cancelling.
            Assert.True(cancel > handled, "Escape must be consumed before it cancels the capture");

            string block = src.Substring(navigate, handled - navigate);
            Assert.Contains("EndCapture();", block);
        }
    }
}
