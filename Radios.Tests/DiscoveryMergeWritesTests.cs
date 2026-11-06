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
        /// transport, not the flag. The merge sends nothing at all, and since
        /// repair round 2 it does not take the list's identity either: with the
        /// transport up the radio can report for itself (this test said the
        /// list's name was taken here until that round; the update now needs
        /// a radio we are not talking to, below).
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
            Assert.Equal("NewShack", vendor.Radio.Nickname);
            Assert.Equal("K0NEW", vendor.Radio.Callsign);

            // A radio we are not talking to takes the list's identity.
            var away = RadioThatSays("NewShack", "K0NEW", connected: false);
            DropTransport(away);
            MergeInto(away.Radio, StaleListCopy());
            Assert.Equal("OldShack", away.Radio.Nickname);
            Assert.Equal("W0OLD", away.Radio.Callsign);
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
            // The transport is up in both cases, so the radio's own word stands
            // (repair round 2: the not-connected case took the packet's
            // identity until then).
            Assert.Equal("NewShack", vendor.Radio.Nickname);
            Assert.Equal("K0NEW", vendor.Radio.Callsign);
        }

        /// <summary>
        /// The LAN refresh's other side: a radio we are not talking to takes
        /// the packet's identity, with nothing sent.
        /// </summary>
        [Fact]
        public void A_lan_discovery_refresh_updates_a_radio_we_are_not_talking_to()
        {
            var vendor = RadioThatSays("NewShack", "K0NEW", connected: false);
            typeof(Radio).GetField("_ip", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(vendor.Radio, System.Net.IPAddress.Loopback);
            typeof(Radio).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(vendor.Radio, "FLEX-6400");
            DropTransport(vendor);

            RefreshRadio!.Invoke(null, new object[] { vendor.Radio, StaleListCopy() });

            Assert.Equal("OldShack", vendor.Radio.Nickname);
            Assert.Equal("W0OLD", vendor.Radio.Callsign);
        }

        /// <summary>
        /// The class, pinned at the source: no discovery or list merge onto an
        /// existing radio object uses the sending setters. API.cs is the
        /// vendor's merge; FlexBase's is ours. (Discovery.cs and WanServer.cs
        /// set identity only on a Radio they have just constructed, before the
        /// new object is published or connected, so nothing can be sent there.)
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

        /// <summary>Take the fake command transport down, as a radio we are not talking to has it.</summary>
        internal static void DropTransport(VendorRadioFixture vendor)
        {
            var current = typeof(CommandCommunicationBase).GetProperty("CurrentConnection")!.GetValue(vendor.Transport);
            typeof(CommandCommunicationBase).GetMethod("PublishDisconnected", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(vendor.Transport, new[] { current });
            Assert.False(vendor.Transport.IsConnected, "The harness meant a transport that is down.");
        }

        /// <summary>
        /// Repair round 2, Codex B2. A list copy starts on a radio we are not
        /// talking to; while it is part-way through, the radio connects and
        /// reports its own name and callsign. The copy must not then finish
        /// over the radio's report. Until this round the copy wrote the name,
        /// raised its notification (here the connect and the report land), and
        /// then wrote the stale callsign over the radio's own: a mixed,
        /// poisoned identity that Radio Info could later send back.
        /// </summary>
        [Fact]
        public void A_list_copy_cannot_finish_over_a_report_the_radio_made_meanwhile()
        {
            var vendor = RadioThatSays("NewShack", "K0NEW", connected: false);
            DropTransport(vendor);
            NicknameField.SetValue(vendor.Radio, "MidShack");
            CallsignField.SetValue(vendor.Radio, "K0MID");

            bool reported = false;
            vendor.Radio.PropertyChanged += (_, e) =>
            {
                if (reported || e.PropertyName != nameof(Radio.Nickname)) return;
                reported = true;
                // The radio connects and says who it is, in the middle of the copy.
                typeof(Radio).GetProperty(nameof(Radio.Connected))!.GetSetMethod(nonPublic: true)!
                    .Invoke(vendor.Radio, new object[] { true });
                vendor.Status("radio callsign=K0NEW nickname=NewShack");
            };

            vendor.Radio.UpdateIdentityFromList("OldShack", "W0OLD");

            Assert.True(reported, "The schedule never ran: no notification came while the copy was in progress.");
            Assert.Equal("NewShack", vendor.Radio.Nickname);
            Assert.Equal("K0NEW", vendor.Radio.Callsign);
        }

        /// <summary>
        /// And the copy itself refuses once the radio is talking to us: flagged
        /// connected, or its command transport up (an administrative
        /// connection parses the radio's reports while the flag reads false).
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_list_copy_is_refused_while_the_radio_can_report_for_itself(bool flaggedConnected)
        {
            var vendor = RadioThatSays("NewShack", "K0NEW", connected: flaggedConnected);
            Assert.True(vendor.Transport.IsConnected);

            vendor.Radio.UpdateIdentityFromList("OldShack", "W0OLD");

            Assert.Equal("NewShack", vendor.Radio.Nickname);
            Assert.Equal("K0NEW", vendor.Radio.Callsign);
            Assert.False(CommandsIdentity(vendor));
        }

        /// <summary>The control: a radio we are not talking to takes the list's identity, and nothing is sent.</summary>
        [Fact]
        public void A_list_copy_updates_a_radio_we_are_not_talking_to()
        {
            var vendor = RadioThatSays("NewShack", "K0NEW", connected: false);
            DropTransport(vendor);

            vendor.Radio.UpdateIdentityFromList("OldShack", "W0OLD");

            Assert.Equal("OldShack", vendor.Radio.Nickname);
            Assert.Equal("W0OLD", vendor.Radio.Callsign);
            Assert.False(CommandsIdentity(vendor));
        }

        /// <summary>
        /// Radio Info sends a callsign or a name ONLY when the operator changed
        /// the field in that dialog (repair round 2, Codex B2's second half).
        /// It compared the box with the LIVE value on focus loss, so a box
        /// holding a value the radio has since corrected was sent back, with
        /// nobody having typed anything.
        /// </summary>
        [Fact]
        public void Radio_info_sends_identity_only_when_the_operator_edited_it()
        {
            string dialog = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "Dialogs", "RadioInfoDialog.xaml.cs"));
            foreach (string handler in new[]
            {
                "private void CallBox_LostFocus(", "private void CallBox_KeyDown(",
                "private void NameBox_LostFocus(", "private void NameBox_KeyDown(",
            })
            {
                string body = Body(dialog, handler);
                Assert.DoesNotContain("_callbacks.GetCallsign()", body, StringComparison.Ordinal);
                Assert.DoesNotContain("_callbacks.GetNickname()", body, StringComparison.Ordinal);
            }

            // Repair round 3, Codex F4: the commit helpers and the baselines.
            // Each box and its baseline are filled together; each commit goes
            // through the one tested decision, with its own box, callbacks and
            // rule, and assigns the baseline from its result.
            string show = Body(dialog, "private void ShowValues(");
            Assert.Contains("CallBox.Text = _shownCallsign = ", show, StringComparison.Ordinal);
            Assert.Contains("NameBox.Text = _shownNickname = ", show, StringComparison.Ordinal);
            string call = dialog.Substring(dialog.IndexOf("private void CommitCallsignIfEdited()", StringComparison.Ordinal));
            call = call.Substring(0, call.IndexOf(';') + 1);
            Assert.Contains("_shownCallsign = CommitIdentity(_shownCallsign, CallBox.Text,", call, StringComparison.Ordinal);
            Assert.Contains("_callbacks.SetCallsign, _callbacks.GetCallsign, Radios.FlexBase.CallsignAsTheRadioKeepsIt", call, StringComparison.Ordinal);
            string name = dialog.Substring(dialog.IndexOf("private void CommitNicknameIfEdited()", StringComparison.Ordinal));
            name = name.Substring(0, name.IndexOf(';') + 1);
            Assert.Contains("_shownNickname = CommitIdentity(_shownNickname, NameBox.Text,", name, StringComparison.Ordinal);
            Assert.Contains("_callbacks.SetNickname(name), _callbacks.GetNickname, Radios.FlexBase.NicknameAsTheRadioKeepsIt", name, StringComparison.Ordinal);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(dialog, @"_callbacks\.Set(Callsign|Nickname)\b").Count);
        }

        /// <summary>The FlexLib patch that makes the update silent, pinned at its source (MIGRATION.md item 24).</summary>
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

        internal static string Body(string text, string signature)
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

        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                dir = dir.Parent;
            Assert.True(dir != null, "Could not find the repository root above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }
    }
}
