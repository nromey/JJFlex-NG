#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The #397 write-path audit's two sharpest remaining writers (#477),
    /// HARM-OTHERS. A SmartLink list merge must never command a radio's name
    /// or callsign, and the method that wrote one account's port into another
    /// account's radio stays deleted, with the Network tab's Apply the only
    /// port writer in the tree.
    /// </summary>
    public sealed class DiscoveryMergeWritesTests : IDisposable
    {
        private const string Serial = "1111-2222-6300-4444";
        private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        public void Dispose() => _rig.Dispose();

        private static readonly MethodInfo Merge =
            typeof(FlexBase).GetMethod("UpdateRadioDiscoveryFields", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static readonly FieldInfo NicknameField =
            typeof(Radio).GetField("_nickname", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo CallsignField =
            typeof(Radio).GetField("_callsign", BindingFlags.NonPublic | BindingFlags.Instance)!;

        /// <summary>A radio as a SmartLink list carries it: an older name and callsign than the radio holds.</summary>
        private static Radio StaleListCopy()
        {
            var listed = SmartLinkRegistrationReplayTests.WanRadio(Serial);
            NicknameField.SetValue(listed, "OldShack");
            CallsignField.SetValue(listed, "W0OLD");
            return listed;
        }

        private static VendorRadioFixture RadioThatSays(string name, string callsign, bool connected)
        {
            Assert.True(Merge != null && NicknameField != null && CallsignField != null,
                "The merge or the radio's name fields are not where this test reaches them, so it would prove nothing.");
            var vendor = new VendorRadioFixture(Serial);
            NicknameField.SetValue(vendor.Radio, name);
            CallsignField.SetValue(vendor.Radio, callsign);
            if (!connected)
            {
                typeof(Radio).GetProperty(nameof(Radio.Connected))!.GetSetMethod(nonPublic: true)!
                    .Invoke(vendor.Radio, new object[] { false });
            }
            return vendor;
        }

        private void MergeInto(Radio target, Radio listed) =>
            Merge.Invoke(_rig, new object[] { listed, target, "owner@example.com" });

        private static bool CommandsIdentity(VendorRadioFixture vendor) =>
            vendor.Transport.Commands.Any(c =>
                c.StartsWith("radio name", StringComparison.Ordinal)
                || c.StartsWith("radio callsign", StringComparison.Ordinal));

        /// <summary>
        /// The live object a connect adopts as the merge target. Before #477
        /// the merge set Nickname and Callsign through FlexLib's setters, which
        /// send "radio name" and "radio callsign", which the radio keeps in its
        /// firmware: a stale list wrote an old name back into somebody's radio.
        /// </summary>
        [Fact]
        public void A_list_merge_sends_no_name_or_callsign_to_a_connected_radio()
        {
            var vendor = RadioThatSays("NewShack", "K0NEW", connected: true);

            MergeInto(vendor.Radio, StaleListCopy());

            Assert.False(CommandsIdentity(vendor),
                "A SmartLink list merge commanded a connected radio's name or callsign: "
                + string.Join(" | ", vendor.Transport.Commands));
            // And the radio's own word stands: the list's lagging copy is not applied.
            Assert.Equal("NewShack", vendor.Radio.Nickname);
            Assert.Equal("K0NEW", vendor.Radio.Callsign);
        }

        /// <summary>
        /// The race the connected check alone would leave: the command
        /// transport is up but the radio object does not read Connected yet.
        /// The setters would send then, because FlexLib's SendCommand asks the
        /// transport, not the flag. The merge's update sends nothing at all,
        /// and the picker's copy still takes the list's name.
        /// </summary>
        [Fact]
        public void A_list_merge_updates_a_radio_not_yet_connected_without_sending()
        {
            var vendor = RadioThatSays("NewShack", "K0NEW", connected: false);
            Assert.True(vendor.Transport.IsConnected,
                "The harness meant a transport that is up under a radio not flagged connected.");

            MergeInto(vendor.Radio, StaleListCopy());

            Assert.False(CommandsIdentity(vendor),
                "A SmartLink list merge sent a name or callsign: " + string.Join(" | ", vendor.Transport.Commands));
            Assert.Equal("OldShack", vendor.Radio.Nickname);
            Assert.Equal("W0OLD", vendor.Radio.Callsign);
        }

        /// <summary>
        /// The positive control for both tests above: the same harness DOES see
        /// the command when one is sent, so "nothing was sent" is a finding.
        /// </summary>
        [Fact]
        public void The_harness_sees_a_name_command_when_one_is_sent()
        {
            var vendor = RadioThatSays("NewShack", "K0NEW", connected: true);
            vendor.Radio.Nickname = "TypedByOperator";
            Assert.True(CommandsIdentity(vendor));
        }

        private static readonly MethodInfo? RefreshRadio =
            typeof(Flex.Smoothlake.FlexLib.API).GetMethod("RefreshRadio", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>
        /// FlexLib's own LAN discovery refresh (repair round 1, Codex B1).
        /// Every discovery packet for a radio already in the list is merged
        /// into the EXISTING object, the one a LAN connect adopts, through
        /// API.RefreshRadio, and that used the sending setters too: a packet
        /// that lagged a rename sent "radio name" with the old name. Driven
        /// here through the vendor's own method, with the transport up, the
        /// radio both flagged connected and not.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_lan_discovery_refresh_sends_no_name_or_callsign(bool connected)
        {
            Assert.True(RefreshRadio != null, "API.RefreshRadio is not where this test reaches it, so it would prove nothing.");
            var vendor = RadioThatSays("NewShack", "K0NEW", connected);
            Assert.True(vendor.Transport.IsConnected);
            // The fixture builds a bare radio; the vendor's discovery log line
            // formats its address and model, so give it the ones a discovered
            // radio has.
            typeof(Radio).GetField("_ip", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(vendor.Radio, System.Net.IPAddress.Loopback);
            typeof(Radio).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(vendor.Radio, "FLEX-6400");

            RefreshRadio!.Invoke(null, new object[] { vendor.Radio, StaleListCopy() });

            Assert.False(CommandsIdentity(vendor),
                "FlexLib's LAN discovery refresh commanded a radio's name or callsign: "
                + string.Join(" | ", vendor.Transport.Commands));
            if (connected)
            {
                // The radio's own status is the authority while connected.
                Assert.Equal("NewShack", vendor.Radio.Nickname);
                Assert.Equal("K0NEW", vendor.Radio.Callsign);
            }
            else
            {
                Assert.Equal("OldShack", vendor.Radio.Nickname);
                Assert.Equal("W0OLD", vendor.Radio.Callsign);
            }
        }

        /// <summary>
        /// The class, pinned at the source: no discovery or list merge onto an
        /// existing radio object uses the sending setters. API.cs is the
        /// vendor's merge; FlexBase's is ours. (Discovery.cs and WanServer.cs
        /// set identity only on a Radio they have just constructed, whose
        /// transport does not exist yet, so nothing can be sent there.)
        /// </summary>
        [Fact]
        public void No_list_or_discovery_merge_uses_the_sending_identity_setters()
        {
            string api = File.ReadAllText(Path.Combine(RepoRoot(), "FlexLib_API", "FlexLib", "API.cs"));
            string refresh = Body(api, "private static void RefreshRadio(");
            Assert.DoesNotContain(".Nickname = ", refresh, StringComparison.Ordinal);
            Assert.DoesNotContain(".Callsign = ", refresh, StringComparison.Ordinal);
            Assert.Contains("UpdateIdentityFromList(", refresh, StringComparison.Ordinal);
            Assert.Contains("JJFlex patch", refresh, StringComparison.Ordinal);
        }

        /// <summary>The FlexLib patch that makes the update silent, pinned at its source (MIGRATION.md item 23).</summary>
        [Fact]
        public void The_flexlib_identity_update_is_marked_and_sends_nothing()
        {
            string radio = File.ReadAllText(Path.Combine(RepoRoot(), "FlexLib_API", "FlexLib", "Radio.cs"));
            int at = radio.IndexOf("public void UpdateIdentityFromList(", StringComparison.Ordinal);
            Assert.True(at > 0, "UpdateIdentityFromList is gone from the vendored Radio.cs.");
            string before = radio.Substring(Math.Max(0, at - 900), Math.Min(900, at));
            Assert.Contains("JJFlex patch: identity from a radio LIST", before, StringComparison.Ordinal);
            string body = Body(radio, "public void UpdateIdentityFromList(");
            Assert.DoesNotContain("SendCommand", body, StringComparison.Ordinal);

            string merge = Body(FlexBasePartials.JoinedText(), "private void UpdateRadioDiscoveryFields(");
            Assert.DoesNotContain(".Nickname = ", merge, StringComparison.Ordinal);
            Assert.DoesNotContain(".Callsign = ", merge, StringComparison.Ordinal);
            Assert.Contains("UpdateIdentityFromList(", merge, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // The port writer that took a radio off the air (2026-08-29)
        // ------------------------------------------------------------------

        /// <summary>
        /// The method behind the 2026-08-29 incident is deleted, and stays so.
        /// Its name survives only in the incident's own record, in comments.
        /// </summary>
        [Fact]
        public void The_on_connect_port_writer_stays_deleted()
        {
            var hits = CodeLinesMentioning("ApplyAccountPortPreferenceIfAny");
            Assert.True(hits.Count == 0,
                "ApplyAccountPortPreferenceIfAny is back in code: " + string.Join("; ", hits));
            Assert.Null(typeof(FlexBase).GetMethod("ApplyAccountPortPreferenceIfAny",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        }

        /// <summary>
        /// #397's item 5, in the only form it can take now. Once the deleted
        /// method is gone, nothing on the connect path writes a port at all,
        /// so a behavioural "connect under one account writes no port into
        /// another's radio" has nothing to catch and its control could not go
        /// red. The pinned form instead: the only call of
        /// SetSmartLinkPortForwarding in the tree is the Network tab's Apply.
        /// </summary>
        [Fact]
        public void The_network_tabs_apply_is_the_only_port_writer()
        {
            var calls = PortWriteCalls(AllSources());
            Assert.True(calls.Count == 1 && calls[0].file.EndsWith("JJFlexWpf/Dialogs/SettingsDialog.xaml.cs", StringComparison.Ordinal),
                "SetSmartLinkPortForwarding is called from somewhere other than the Network tab's Apply: "
                + string.Join("; ", calls.Select(c => c.file + ": " + c.line)));
            string settings = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));
            Assert.Contains("SetSmartLinkPortForwarding(", Body(settings, "private bool PerformPortForwardApply("), StringComparison.Ordinal);
        }

        /// <summary>The control: a planted second caller is caught, and a mention in a comment or a trace string is not a caller.</summary>
        [Fact]
        public void The_port_writer_scan_catches_a_planted_second_caller()
        {
            var planted = new List<(string file, string text)>
            {
                ("JJFlexWpf/Dialogs/SettingsDialog.xaml.cs", "            bool ok = _rig.SetSmartLinkPortForwarding(enabled, tcp, udp);"),
                ("Radios/FlexBase.cs", "            bool ok = SetSmartLinkPortForwarding(true, port, port);"),
                ("Radios/FlexBase.cs", "            // the Network tab writes through FlexBase.SetSmartLinkPortForwarding("),
                ("Radios/FlexBase.cs", "            Tracing.TraceLine(\"SetSmartLinkPortForwarding(\" + x);"),
            };
            var calls = PortWriteCalls(planted);
            Assert.Equal(2, calls.Count);
            Assert.Contains(calls, c => c.file == "Radios/FlexBase.cs");
        }

        private static readonly Regex PortWriteCall = new Regex(@"(?<![A-Za-z_])SetSmartLinkPortForwarding\s*\(", RegexOptions.Compiled);

        private static List<(string file, string line)> PortWriteCalls(IEnumerable<(string file, string text)> sources)
        {
            var found = new List<(string, string)>();
            foreach (var (file, text) in sources)
            {
                foreach (string raw in text.Split('\n'))
                {
                    string line = CodeOnly(raw);
                    if (!PortWriteCall.IsMatch(line)) continue;
                    if (line.Contains("public bool SetSmartLinkPortForwarding(", StringComparison.Ordinal)) continue;
                    found.Add((file, raw.Trim()));
                }
            }
            return found;
        }

        /// <summary>A line with its comment and its string literals removed: what the compiler sees as code.</summary>
        private static string CodeOnly(string line)
        {
            string noStrings = Regex.Replace(line, "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");
            int comment = noStrings.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) noStrings = noStrings.Substring(0, comment);
            string trimmed = noStrings.TrimStart();
            if (trimmed.StartsWith("'", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal)) return "";
            return noStrings;
        }

        private static List<string> CodeLinesMentioning(string symbol)
        {
            var hits = new List<string>();
            foreach (var (file, text) in AllSources())
            {
                string[] lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    if (CodeOnly(lines[i]).Contains(symbol, StringComparison.Ordinal))
                        hits.Add(file + ":" + (i + 1));
            }
            return hits;
        }

        private static List<(string file, string text)> AllSources()
        {
            string root = RepoRoot();
            var list = new List<(string, string)>();
            foreach (string dir in new[] { "Radios", "JJFlexWpf" })
            {
                foreach (string f in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                    if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
                    list.Add((rel, File.ReadAllText(f)));
                }
            }
            foreach (string f in Directory.EnumerateFiles(root, "*.vb", SearchOption.TopDirectoryOnly))
                list.Add((Path.GetFileName(f), File.ReadAllText(f)));
            Assert.True(list.Count > 300, "The scan saw only " + list.Count + " files, so it was not looking at the source tree.");
            return list;
        }

        private static string Body(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " is not where this test reads it.");
            int open = text.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(start, i - start + 1);
            }
            Assert.Fail("The body of " + signature + " never closes.");
            return "";
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                dir = dir.Parent;
            Assert.True(dir != null, "Could not find the repository root above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }
    }
}
