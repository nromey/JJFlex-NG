using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #846, the half that needs a window and so cannot run here: the focus
    /// event and the key backstop really call the rule, and the rule keeps
    /// the layer's changes. The rule's own behaviour is proved windowless by
    /// JJFlexWpf.Tests.LayerOutsideItsWindowTests; the harness presses it.
    /// </summary>
    public sealed class LayerWindowWiringTests
    {
        private static string Repo()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
            Assert.True(d != null, "could not find the repository root");
            return d!.FullName;
        }

        private static string Read(string relative) => File.ReadAllText(Path.Combine(Repo(), relative));

        [Fact]
        public void Focus_landing_in_a_window_of_ours_asks_the_rule()
        {
            string keys = Read("JJFlexWpf/KeyCommands.cs");
            Assert.Contains("System.Windows.Input.Keyboard.GotKeyboardFocusEvent", keys, StringComparison.Ordinal);
            Assert.Contains("new System.Windows.Input.KeyboardFocusChangedEventHandler(AnyWindowGotKeyboardFocus)", keys, StringComparison.Ordinal);
            Assert.Contains("handledEventsToo: true", keys, StringComparison.Ordinal);
        }

        [Fact]
        public void The_key_backstop_runs_before_anything_else_in_a_dialog()
        {
            string keys = Read("JJFlexWpf/KeyCommands.cs");
            int handler = keys.IndexOf("private static void AnyWindowPreviewKeyDown(", StringComparison.Ordinal);
            Assert.True(handler >= 0);
            int rule = keys.IndexOf("kc.LetGoOfLayerOutsideItsWindow(sender", handler, StringComparison.Ordinal);
            int transmit = keys.IndexOf("DialogTransmitKeys.OnPreviewKeyDown(sender, e)", handler, StringComparison.Ordinal);
            Assert.True(rule > 0 && rule < transmit, "the rule must run before the dialog's first key handling");
        }

        [Fact]
        public void A_help_surface_is_marked_before_it_can_take_the_keyboard()
        {
            string surfaces = Read("JJFlexWpf/KeyHelpSurfaces.cs");
            int attach = surfaces.IndexOf("public static void Attach(Window window)", StringComparison.Ordinal);
            int mark = surfaces.IndexOf("_surfaces.AddOrUpdate(window", attach, StringComparison.Ordinal);
            int loaded = surfaces.IndexOf("window.Loaded +=", attach, StringComparison.Ordinal);
            Assert.True(mark > 0 && mark < loaded, "marked at Attach, not at Loaded");
        }
    }
}
