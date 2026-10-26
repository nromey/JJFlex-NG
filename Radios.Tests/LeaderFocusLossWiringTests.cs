using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #693 repair round: the shell tells the key layer when another program
    /// takes the foreground. The release itself is driven, window-free, by
    /// JJFlexWpf.Tests' LeaderFocusLossTests; this reads the VB shell, which
    /// needs a window and cannot be constructed in a test.
    /// </summary>
    public sealed class LeaderFocusLossWiringTests
    {
        [Fact]
        public void The_shell_releases_a_waiting_JJ_key_layer_when_the_app_loses_the_foreground()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "BridgeForm.vb"));

            Assert.Contains("Private Const WM_ACTIVATEAPP As Integer = &H1C", src);

            int wndProc = src.IndexOf("Protected Overrides Sub WndProc(ByRef m As Message)", StringComparison.Ordinal);
            Assert.True(wndProc >= 0, "BridgeForm.WndProc was not found");
            int end = src.IndexOf("End Sub", wndProc, StringComparison.Ordinal);
            string body = src.Substring(wndProc, end - wndProc);

            // WParam zero is "this application is being DEACTIVATED"; non-zero
            // is the activation, which must release nothing.
            int test = body.IndexOf("If m.Msg = WM_ACTIVATEAPP AndAlso m.WParam = IntPtr.Zero Then", StringComparison.Ordinal);
            Assert.True(test >= 0, "WndProc no longer answers WM_ACTIVATEAPP's deactivation");
            int call = body.IndexOf("JJFlexWpf.KeyCommands.ApplicationDeactivated()", test, StringComparison.Ordinal);
            int endIf = body.IndexOf("End If", test, StringComparison.Ordinal);
            Assert.True(call > test && call < endIf,
                "the deactivation no longer reaches KeyCommands.ApplicationDeactivated, so a waiting layer outlives the window");
        }

        [Fact]
        public void The_release_reaches_the_routing_owner_and_lets_go_of_both_one_shot_waits()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf/KeyCommands.cs"));
            int at = src.IndexOf("public static void ApplicationDeactivated()", StringComparison.Ordinal);
            Assert.True(at >= 0, "KeyCommands.ApplicationDeactivated was not found");
            string head = src.Substring(at, 200);
            Assert.Contains("_globalRoutingOwner?.ReleaseOneShotWaits(", head);

            // And it must not relay to the hook: that choice is the point.
            int body = src.IndexOf("internal void ReleaseOneShotWaits(string why)", StringComparison.Ordinal);
            Assert.True(body >= 0);
            int bodyEnd = src.IndexOf("\n    }", body, StringComparison.Ordinal);
            string release = src.Substring(body, bodyEnd - body);
            Assert.DoesNotContain("NoteHelpArmed", release);
        }
    }
}
