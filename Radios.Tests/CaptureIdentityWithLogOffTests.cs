using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A detailed capture started with the standing log off says which build
    /// and which settings tree made it (#1057).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> <c>TraceCoordinator.AppIdentity</c> was assigned
    /// inside <c>If BootTrace Then</c> in <c>GetConfigInfo</c>, and
    /// <c>BootTrace</c> is false when the operator keeps no standing log. The
    /// closing <c>CaptureState:</c> line is rendered from that identity, so a
    /// capture made with the standing log off ended
    /// <c>version=unknown app=</c>. The two <c>ConfigLocation:</c> lines sat
    /// in the same block, so the same capture under a throwaway settings tree
    /// never said so.
    /// </para>
    /// <para>
    /// <b>What these can see.</b> The VB startup is reached only by launching
    /// the app, so the order is read from source. <b>What they cannot
    /// see:</b> the archived capture itself; a run under a throwaway tree with
    /// the standing log off, Ctrl+J then Ctrl+D twice, settles that.
    /// </para>
    /// </remarks>
    public sealed class CaptureIdentityWithLogOffTests
    {
        private static string Globals() =>
            File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "globals.vb")).Replace("\r\n", "\n");

        /// <summary>A VB member from its signature to its own closing line.</summary>
        private static string Member(string src, string signature, string end)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " is missing");
            int stop = src.IndexOf(end, start, StringComparison.Ordinal);
            Assert.True(stop > start, end + " not found after " + signature);
            return src.Substring(start, stop - start);
        }

        private const string AppIdentityAssignment = "TraceCoordinator.AppIdentity = New TraceEnvironment With {";

        private static string GetConfigInfo() =>
            Member(Globals(), "Friend Sub GetConfigInfo()", "\n    End Sub\n");

        [Fact]
        public void The_identity_is_set_before_the_standing_log_decision_not_inside_it()
        {
            string body = GetConfigInfo();
            int identity = body.IndexOf(AppIdentityAssignment, StringComparison.Ordinal);
            int decision = body.IndexOf("BootTrace = (Not Debugger.IsAttached)", StringComparison.Ordinal);
            int block = body.IndexOf("\n        If BootTrace Then\n", StringComparison.Ordinal);

            Assert.True(identity >= 0, "GetConfigInfo assigns the identity");
            Assert.True(block > 0, "the standing log's block is where it was");
            Assert.True(identity < block, "the identity is assigned before If BootTrace Then, so it is set either way");
            Assert.True(decision >= 0 && decision < identity, "and after the decision it no longer depends on");

            // Once, in the whole file: nothing still assigns it inside the block.
            Assert.Single(Regex.Matches(Globals(), Regex.Escape(AppIdentityAssignment)));
        }

        [Fact]
        public void The_identity_carries_the_version_and_the_app_path()
        {
            string body = GetConfigInfo();
            int identity = body.IndexOf(AppIdentityAssignment, StringComparison.Ordinal);
            string assignment = body.Substring(identity, body.IndexOf("\n        }\n", identity, StringComparison.Ordinal) - identity);
            Assert.Contains(".Instance = ProgramInstance", assignment);
            Assert.Contains(".AppVersion = If(myVersion IsNot Nothing, myVersion.ToString(), \"unknown\")", assignment);
            Assert.Contains(".AppPath = If(myAssembly IsNot Nothing, myAssembly.Location, String.Empty)", assignment);

            // And everything it reads is set earlier in the same method.
            Assert.True(body.IndexOf("ProgramInstance = ", StringComparison.Ordinal) < identity);
            Assert.True(body.IndexOf("myVersion = ", StringComparison.Ordinal) < identity);
        }

        [Fact]
        public void Positive_control_the_scan_finds_the_boot_marker_inside_the_standing_log_block()
        {
            // If the block were not where the scan thinks it is, the order
            // test above could pass against the wrong text.
            string body = GetConfigInfo();
            int block = body.IndexOf("\n        If BootTrace Then\n", StringComparison.Ordinal);
            int marker = body.IndexOf("TraceCaptureStateMarker()", block, StringComparison.Ordinal);
            int after = body.IndexOf("Tracing.TraceLine(\"GetConfigInfo:\"", StringComparison.Ordinal);
            Assert.True(block > 0 && marker > block && after > marker,
                "the boot CaptureState marker sits inside If BootTrace Then, before the GetConfigInfo line");
        }

        [Fact]
        public void Both_settings_location_lines_live_in_one_helper()
        {
            string src = Globals();
            string helper = Member(src, "Private Sub TraceConfigLocation()", "\n    End Sub\n");
            Assert.Contains("If UsingTemporaryConfigDir Then", helper);
            Assert.Contains("\"ConfigLocation: TEMPORARY settings tree in use at \" & BaseConfigDir", helper);
            Assert.Contains("If ConfigDirRefusal IsNot Nothing Then", helper);
            Assert.Contains("Tracing.TraceLine(\"ConfigLocation: \" & ConfigDirRefusal, TraceLevel.Warning)", helper);

            // No second copy of either line anywhere else in the file.
            Assert.Equal(2, Regex.Matches(src, "\"ConfigLocation: ").Count);
        }

        [Fact]
        public void Boot_still_states_the_settings_location_when_the_standing_log_is_on()
        {
            string body = GetConfigInfo();
            int block = body.IndexOf("\n        If BootTrace Then\n", StringComparison.Ordinal);
            int call = body.IndexOf("TraceConfigLocation()", StringComparison.Ordinal);
            int marker = body.IndexOf("TraceCaptureStateMarker()", block, StringComparison.Ordinal);
            Assert.True(call > block && call < marker, "boot writes the location inside the block, before its marker, as before");
        }

        [Fact]
        public void Every_detailed_capture_states_the_settings_location()
        {
            string start = Member(Globals(), "Friend Sub StartDetailedCapture(", "\n    End Sub\n");
            int opened = start.IndexOf("Dim opened As TraceTransitionResult = BeginCaptureSession(reason)", StringComparison.Ordinal);
            int marker = start.IndexOf("TraceCaptureStateMarker()", StringComparison.Ordinal);
            int call = start.IndexOf("TraceConfigLocation()", StringComparison.Ordinal);
            int failed = start.IndexOf("Catch ex As Exception", StringComparison.Ordinal);

            Assert.True(opened > 0, "the capture opens in StartDetailedCapture");
            Assert.True(call > opened, "the location is written after the capture opened, so it lands in the capture file");
            Assert.True(call > marker, "beside the capture's own CaptureState marker");
            Assert.True(call < failed, "inside the capture's Try, not in its failure path");
        }
    }
}
