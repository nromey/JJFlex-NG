#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The radio's identity fields and the gate that guards them (#477,
    /// MIGRATION.md item 24), repair round 3. Two things are held here:
    /// a callsign or name the operator types is the one put on the wire,
    /// whatever the radio reports meanwhile (Codex B3); and every writer of
    /// those fields waits for the gate, pinned by behaviour and at the source
    /// (Codex F4).
    /// </summary>
    public sealed class IdentityGateTests
    {
        private const string Serial = "1111-2222-6300-4444";

        private static readonly FieldInfo GateField =
            typeof(Radio).GetField("_identityGate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo HookField =
            typeof(Radio).GetField("AfterIdentityCached", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo NicknameField =
            typeof(Radio).GetField("_nickname", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo CallsignField =
            typeof(Radio).GetField("_callsign", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo ConnectedField =
            typeof(Radio).GetField("_connected", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo UpdateInfoMethod =
            typeof(Radio).GetMethod("UpdateInfo", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static VendorRadioFixture RadioNamed(string name, string callsign)
        {
            Assert.True(GateField != null && HookField != null && NicknameField != null && CallsignField != null
                && ConnectedField != null && UpdateInfoMethod != null,
                "The identity gate, its fields or its seam are not where this test reaches them, so it would prove nothing.");
            var vendor = new VendorRadioFixture(Serial);
            NicknameField.SetValue(vendor.Radio, name);
            CallsignField.SetValue(vendor.Radio, callsign);
            return vendor;
        }

        private static void InfoReply(VendorRadioFixture vendor, string body) =>
            UpdateInfoMethod.Invoke(vendor.Radio, new object[] { 0, 0u, body });

        private static void SetConnected(Radio radio, bool value) =>
            typeof(Radio).GetProperty(nameof(Radio.Connected))!.GetSetMethod(nonPublic: true)!
                .Invoke(radio, new object[] { value });

        // ------------------------------------------------------------------
        // Codex B3: the wire carries what the operator typed
        // ------------------------------------------------------------------

        /// <summary>
        /// The operator sets a callsign or a name; after the setter has
        /// cached it and before the command is built, the radio's own report
        /// lands on the reader thread. Until round 3 each setter built its
        /// command by reading the shared field AGAIN, so it sent the
        /// reported identity, which nobody typed. The command is now built
        /// from the setter's own sanitized value.
        /// </summary>
        [Theory]
        [InlineData("callsign", "status")]
        [InlineData("callsign", "info")]
        [InlineData("name", "status")]
        [InlineData("name", "info")]
        public void The_identity_sent_is_the_one_typed_even_when_a_report_lands_first(string field, string report)
        {
            var vendor = RadioNamed("OldShack", "W0OLD");
            bool reported = false;
            Action land = () =>
            {
                if (reported) return;
                reported = true;
                // On another thread, as the reader would: the setter must not
                // be holding anything the report needs.
                Assert.True(Task.Run(() =>
                {
                    if (report == "status") vendor.Status("radio callsign=W0REPORT nickname=ReportShack");
                    else InfoReply(vendor, "callsign=W0REPORT,name=ReportShack");
                }).Wait(5000), "The report could not land: the setter held the gate across its hand-off.");
            };
            HookField.SetValue(vendor.Radio, land);

            if (field == "callsign") vendor.Radio.Callsign = "k0typed";
            else vendor.Radio.Nickname = "TypedShack";

            Assert.True(reported, "The schedule never ran: the setter never reached its hand-off.");
            string expected = field == "callsign" ? "radio callsign K0TYPED" : "radio name TypedShack";
            Assert.Contains(expected, vendor.Transport.Commands);
            Assert.DoesNotContain(vendor.Transport.Commands, c => c.Contains("W0REPORT") || c.Contains("ReportShack"));
        }

        /// <summary>The control: with no report in between, the typed identity is sent, sanitized as the radio keeps it.</summary>
        [Fact]
        public void A_typed_identity_is_sent_as_typed_with_nothing_in_between()
        {
            var vendor = RadioNamed("OldShack", "W0OLD");
            vendor.Radio.Callsign = "k0typed";
            vendor.Radio.Nickname = "Typed Shack";
            Assert.Equal(new[] { "radio callsign K0TYPED", "radio name TypedShack" },
                vendor.Transport.Commands.Where(c => c.StartsWith("radio callsign") || c.StartsWith("radio name")));
        }

        // ------------------------------------------------------------------
        // Codex F4: every writer waits for the gate
        // ------------------------------------------------------------------

        /// <summary>
        /// Each of the eight writers of the radio's identity fields (and of the
        /// Connected flag the list copy checks) is started while the test holds
        /// the identity gate. It must not write until the gate is released,
        /// and must finish once it is. Removing any one lock turns its row red.
        /// </summary>
        [Theory]
        [InlineData("status callsign")]
        [InlineData("status nickname")]
        [InlineData("info callsign")]
        [InlineData("info name")]
        [InlineData("connected flag")]
        [InlineData("list copy")]
        [InlineData("callsign setter")]
        [InlineData("nickname setter")]
        public void Every_identity_writer_waits_for_the_gate(string writer)
        {
            var vendor = RadioNamed("OldShack", "W0OLD");
            if (writer == "list copy")
            {
                SetConnected(vendor.Radio, false);
                DiscoveryMergeWritesTests.DropTransport(vendor);
            }

            Func<object?> read = writer switch
            {
                "status nickname" or "info name" or "nickname setter" or "list copy" => () => NicknameField.GetValue(vendor.Radio),
                "connected flag" => () => ConnectedField.GetValue(vendor.Radio),
                _ => () => CallsignField.GetValue(vendor.Radio),
            };
            Action write = writer switch
            {
                "status callsign" => () => vendor.Status("radio callsign=W0NEW"),
                "status nickname" => () => vendor.Status("radio nickname=NewShack"),
                "info callsign" => () => InfoReply(vendor, "callsign=W0NEW"),
                "info name" => () => InfoReply(vendor, "name=NewShack"),
                "connected flag" => () => SetConnected(vendor.Radio, false),
                "list copy" => () => vendor.Radio.UpdateIdentityFromList("NewShack", "W0NEW"),
                "callsign setter" => () => vendor.Radio.Callsign = "W0NEW",
                _ => () => vendor.Radio.Nickname = "NewShack",
            };
            object? before = read();

            object gate = GateField.GetValue(vendor.Radio)!;
            Task task;
            Monitor.Enter(gate);
            try
            {
                task = Task.Run(write);
                Thread.Sleep(300);
                Assert.Equal(before, read());
                Assert.False(task.IsCompleted, writer + " finished while the identity gate was held.");
            }
            finally
            {
                Monitor.Exit(gate);
            }

            Assert.True(task.Wait(5000), writer + " never finished once the gate was released.");
            Assert.NotEqual(before, read());
        }

        /// <summary>
        /// The same gate at the source: each gated site is marked and takes
        /// the gate; notifications, commands and the seam run only after the
        /// gate is released, so a subscriber can never re-enter part-way
        /// through a write and nothing slow is done while holding it.
        /// </summary>
        [Fact]
        public void The_gate_is_taken_at_every_site_and_released_before_anything_is_said_or_sent()
        {
            string radio = File.ReadAllText(Path.Combine(DiscoveryMergeWritesTests.RepoRoot(), "FlexLib_API", "FlexLib", "Radio.cs"));

            // The two parsers: each identity case takes the gate.
            foreach (string kase in new[] { "case \"callsign\":", "case \"nickname\":", "case \"name\":" })
            {
                int hits = 0;
                for (int at = radio.IndexOf(kase, StringComparison.Ordinal); at >= 0; at = radio.IndexOf(kase, at + 1, StringComparison.Ordinal))
                {
                    string arm = radio.Substring(at, radio.IndexOf("break;", at, StringComparison.Ordinal) - at);
                    if (!arm.Contains("_callsign = value") && !arm.Contains("_nickname = value")) continue;
                    hits++;
                    Assert.Contains("lock (_identityGate)", arm, StringComparison.Ordinal);
                    Assert.Contains("JJFlex patch", arm, StringComparison.Ordinal);
                }
                Assert.True(hits >= 1, kase + " no longer writes an identity field where this pin reads it.");
            }

            // The Connected flag, the list copy and both setters: the gate is
            // taken, and nothing is raised, sent or invoked inside it.
            foreach (string site in new[]
            {
                "public bool Connected", "public void UpdateIdentityFromList(",
                "public string Callsign", "public string Nickname",
            })
            {
                string body = DiscoveryMergeWritesTests.Body(radio, site);
                int lockAt = body.IndexOf("lock (_identityGate)", StringComparison.Ordinal);
                Assert.True(lockAt >= 0, site + " does not take the identity gate.");
                string gated = DiscoveryMergeWritesTests.Body(body.Substring(lockAt), "lock (_identityGate)");
                foreach (string forbidden in new[] { "RaisePropertyChanged", "SendCommand", "AfterIdentityCached" })
                    Assert.DoesNotContain(forbidden, gated, StringComparison.Ordinal);
                int raised = body.IndexOf("RaisePropertyChanged", StringComparison.Ordinal);
                Assert.True(raised < 0 || raised > lockAt + gated.Length,
                    site + " raises a notification before its gated write is complete.");
            }

            // This patch is MIGRATION.md item 24 (the 2026-11-02 batch owns 21
            // to 23 and pins its 23); every gated site cites that number.
            string migration = File.ReadAllText(Path.Combine(DiscoveryMergeWritesTests.RepoRoot(), "MIGRATION.md"));
            Assert.Contains("24. **Identity from a radio list, never a command", migration, StringComparison.Ordinal);
            Assert.DoesNotContain("23. **Identity from a radio list", migration, StringComparison.Ordinal);
            Assert.Equal(0, System.Text.RegularExpressions.Regex.Matches(radio, @"identity[^\n]*MIGRATION\.md item 23").Count);

            // The setters build their command from the value they were given,
            // never from the field a report can change underneath them.
            Assert.Contains("SendCommand(\"radio callsign \" + new_value)", DiscoveryMergeWritesTests.Body(radio, "public string Callsign"), StringComparison.Ordinal);
            Assert.Contains("SendCommand(\"radio name \" + new_value)", DiscoveryMergeWritesTests.Body(radio, "public string Nickname"), StringComparison.Ordinal);
        }
    }
}
