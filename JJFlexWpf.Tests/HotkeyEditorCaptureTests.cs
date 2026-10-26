using System.Threading;
using System.Windows.Input;
using JJFlexWpf.Dialogs;
using Xunit;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace JJFlexWpf.Tests
{
    /// <summary>
    /// #705, ruled by Noel 2026-10-10: while the Hotkey Editor waits for a new
    /// key, three keys get three answers. Tab and Shift+Tab move focus,
    /// Escape cancels, Enter is swallowed with a sentence that names it. The
    /// classification is a pure function on the dialog type and the refusal
    /// is static inventory data, so nothing here constructs a window.
    /// </summary>
    public class HotkeyEditorCaptureTests
    {
        [Theory]
        [InlineData(ModifierKeys.None)]
        [InlineData(ModifierKeys.Shift)]
        public void Tab_and_Shift_Tab_move_focus(ModifierKeys modifiers)
        {
            AssertWindowless();
            Assert.Equal(KeysDialog.CaptureKey.Navigate, KeysDialog.ClassifyCaptureKey(Key.Tab, modifiers));
        }

        [Fact]
        public void Escape_cancels_whatever_is_held_with_it()
        {
            AssertWindowless();
            Assert.Equal(KeysDialog.CaptureKey.Cancel, KeysDialog.ClassifyCaptureKey(Key.Escape, ModifierKeys.None));
            Assert.Equal(KeysDialog.CaptureKey.Cancel, KeysDialog.ClassifyCaptureKey(Key.Escape, ModifierKeys.Control));
        }

        [Fact]
        public void Enter_and_a_Ctrl_Tab_are_swallowed_and_considered()
        {
            AssertWindowless();
            Assert.Equal(KeysDialog.CaptureKey.Consider, KeysDialog.ClassifyCaptureKey(Key.Enter, ModifierKeys.None));
            Assert.Equal(KeysDialog.CaptureKey.Consider, KeysDialog.ClassifyCaptureKey(Key.Tab, ModifierKeys.Control));
            Assert.Equal(KeysDialog.CaptureKey.Modifier, KeysDialog.ClassifyCaptureKey(Key.LeftCtrl, ModifierKeys.Control));
            Assert.Equal(KeysDialog.CaptureKey.Consider, KeysDialog.ClassifyCaptureKey(Key.F5, ModifierKeys.Control));
        }

        [Fact]
        public void Enter_is_refused_by_name_and_still_modifier_independently()
        {
            AssertWindowless();
            Radios.Lexicon.Forget();
            Radios.Lexicon.Load(Radios.Lexicon.Partitions);

            Assert.True(KeyInventory.IsReservedForCapture(WinFormsKeys.Return, out string enter));
            Assert.Equal("Enter can't be assigned to a command", enter);
            Assert.DoesNotContain("reserved for navigation", enter);

            // Kept deliberately (#705): Ctrl+Enter and Ctrl+Tab are as
            // unbindable as Enter and Tab, and the reason names what was pressed.
            Assert.True(KeyInventory.IsReservedForCapture(WinFormsKeys.Return | WinFormsKeys.Control, out string ctrlEnter));
            Assert.Equal("Ctrl+Enter can't be assigned to a command", ctrlEnter);
            Assert.True(KeyInventory.IsReservedForCapture(WinFormsKeys.Tab | WinFormsKeys.Control, out string ctrlTab));
            Assert.Equal("Ctrl+Tab can't be assigned to a command", ctrlTab);

            // The sentence the operator hears, assembled.
            Assert.Equal("Enter can't be assigned to a command. Try another key, or press Escape to cancel.",
                Radios.Lexicon.Get("settings.keys.editor.key_reserved", ("reason", enter)));
        }

        // WPF throws on constructing any element off an STA thread, so an MTA
        // test body cannot put a window on the operator's desktop (the
        // NoiseReductionPerChannelTests pattern).
        private static void AssertWindowless() =>
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
    }
}
