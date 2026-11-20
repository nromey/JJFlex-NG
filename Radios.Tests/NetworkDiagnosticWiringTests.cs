using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The network diagnostic's plumbing (SmartLink connection tiers, Track C):
    /// the test and every reader go through THIS radio's own brokering account,
    /// never the active session; the gate comes before anything is sent; the
    /// Network tab and Radio Setup step 6 show the same three parts from one
    /// method; Copy and Save export them. The Settings dialog cannot be built
    /// without a window, so its wiring is pinned at the source, and FlexBase's
    /// half is driven for real.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class NetworkDiagnosticWiringTests
    {
        private static string Src(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        private static string Body(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "not found: " + signature);
            int open = source.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException("unbalanced: " + signature);
        }

        private static FlexBase Rig() => new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };

        private static void SelectSerial(FlexBase rig, string serial) =>
            typeof(FlexBase).GetProperty(nameof(FlexBase.SelectedRadioSerial))!
                .GetSetMethod(nonPublic: true)!.Invoke(rig, new object[] { serial });

        private static string UniqueSerial() => "DIAGW-" + Guid.NewGuid().ToString("N");

        // ── FlexBase, driven ────────────────────────────────────────────

        [Fact]
        public async Task With_no_radio_connected_the_test_is_not_sent_and_no_account_is_asked()
        {
            var rig = Rig();
            string serial = UniqueSerial();
            SelectSerial(rig, serial);
            Assert.False(rig.IsConnected);
            Assert.Equal(serial, rig.DiagnosticRadioSerial);

            var run = await rig.RunNetworkTestAsync();

            Assert.Equal(NetworkTestGateAnswer.NeedsAConnectedRadio, run.Gate);
            Assert.Same(DiagnosticPairing.None, run.Pairing);
            Assert.Null(run.Report);
        }

        [Fact]
        public async Task With_no_radio_known_at_all_there_is_nothing_to_test()
        {
            var rig = Rig();
            var run = await rig.RunNetworkTestAsync();
            Assert.Equal(NetworkTestGateAnswer.NoRadio, run.Gate);
            Assert.Null(run.Report);
        }

        [Fact]
        public void A_radio_nothing_attributes_has_no_brokering_account_and_no_report()
        {
            var rig = Rig();
            string serial = UniqueSerial();
            var pairing = rig.NetworkDiagnosticPairingFor(serial);
            Assert.Equal(DiagnosticPairingKind.NoBrokeringAccount, pairing.Kind);
            Assert.Null(rig.NetworkReportFor(serial));

            var inputs = rig.NetworkDiagnosticInputsFor(serial);
            Assert.Equal(serial, inputs.Serial);
            Assert.Null(inputs.Probe);
            Assert.Null(inputs.LastConnect);
            Assert.Null(inputs.PortsNow);
            Assert.Null(inputs.RadioForwardingOn);
            // And the sections compose from what the rig gathered.
            var s = rig.NetworkDiagnosticFor(serial);
            Assert.Equal(Lexicon.Get("settings.network.diag.policy_no_account", ("radioName", inputs.RadioName)), s.PolicyNow.Lines[0]);
        }

        [Fact]
        public void The_last_connects_account_brokers_the_diagnostic_when_nothing_newer_names_one()
        {
            var rig = Rig();
            string serial = UniqueSerial();
            const string account = "nobody-signed-in@example.invalid";
            var ports = new RadioPortReport(
                new AdvertisedPortPair(PortPairSource.Manual, 4994, 4993, true),
                new AdvertisedPortPair(PortPairSource.Upnp, -1, -1, false), false, true);
            var input = new SmartLinkPolicyInput(SmartLinkConnectionMode.ManualPortForwardOnly, true, RadioConnectionPreference.Auto, false, ports);
            Assert.True(SmartLinkConnectRecords.Store(
                SmartLinkConnectRecord.Begin(serial, account, input, SmartLinkMethodPolicy.Decide(input), DateTime.UtcNow)));

            var pairing = rig.NetworkDiagnosticPairingFor(serial);

            // Named by the record, not signed in here: said as such, and never
            // swapped for whatever session is active.
            Assert.Equal(DiagnosticPairingKind.AccountNotSignedIn, pairing.Kind);
            Assert.Equal(account, pairing.Account);
            Assert.Null(rig.NetworkReportFor(serial));
        }

        // ── FlexBase, at the source ─────────────────────────────────────

        [Fact]
        public void The_test_decides_the_gate_then_the_pairing_then_probes_through_the_paired_session()
        {
            string src = Src("Radios", "FlexBase.NetworkDiagnostic.cs");
            string run = Body(src, "public async Task<NetworkTestRun> RunNetworkTestAsync(");
            int gate = run.IndexOf("NetworkTestGate.Decide(", StringComparison.Ordinal);
            int pair = run.IndexOf("NetworkDiagnosticPairingFor(serial)", StringComparison.Ordinal);
            int session = run.IndexOf("PairedSession(pairing)", StringComparison.Ordinal);
            int probe = run.IndexOf("session.RunNetworkDiagnosticAsync(serial", StringComparison.Ordinal);
            Assert.True(gate > 0 && pair > gate && session > pair && probe > session,
                "RunNetworkTestAsync must decide the gate, then the pairing, then probe only through the paired session");
            Assert.DoesNotContain("ActiveSession", src);
            Assert.DoesNotContain(".MostRecentNetworkReport", src);
        }

        [Fact]
        public void The_post_connect_test_goes_through_the_radios_own_account()
        {
            string kick = Body(Src("Radios", "FlexBase.cs"), "private void KickPostConnectNetworkTest(string serial)");
            Assert.Contains("NetworkDiagnosticPairingFor(serial)", kick);
            Assert.DoesNotContain("ActiveSession", kick);
        }

        [Fact]
        public void Nothing_in_FlexBase_reads_the_newest_report_of_any_radio()
        {
            foreach (var file in Directory.GetFiles(Path.Combine(IntegrationPassTree.Root, "Radios"), "FlexBase*.cs"))
            {
                string text = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(text, @"public\s+[\w.?<>]+\s+MostRecentNetworkReport\b"),
                    Path.GetFileName(file) + " exposes the newest report of any radio again");
                Assert.False(Regex.IsMatch(text, @"public\s+async\s+[^\r\n]*\bRunNetworkDiagnosticAsync\(bool"),
                    Path.GetFileName(file) + " probes through the active session again");
            }
        }

        [Fact]
        public void The_spoken_overlay_and_the_help_button_are_paired_with_the_radios_own_account()
        {
            string main = Src("JJFlexWpf", "MainWindow.xaml.cs");
            int at = main.IndexOf("SessionStatusMessages.ForStatusRich(", StringComparison.Ordinal);
            Assert.True(at > 0);
            string before = main.Substring(Math.Max(0, at - 1600), 1600);
            Assert.Contains("RigControl.SessionOverlayInputs(session)", before);
            Assert.DoesNotContain("MostRecentNetworkReport", before);
            Assert.DoesNotContain("CurrentAccountConnectionMode", before);
            Assert.Contains("overlay.Report, overlay.Tier, verbose, overlay.RadioForwardingOn)", main.Substring(at, 200));

            Assert.Contains("public string? CurrentHelpDocFileName => NetworkHelpKeyFor(DiagnosticRadioSerial);", Src("Radios", "FlexBase.cs"));
            string help = Body(Src("Radios", "FlexBase.NetworkDiagnostic.cs"), "public string? NetworkHelpKeyFor(string serial)");
            Assert.Contains("NetworkDiagnosticPairingFor(serial)", help);
            Assert.Contains("GetLastNetworkReport(serial)", help);
        }

        [Fact]
        public void The_network_identity_card_reads_the_radios_own_test()
        {
            string text = Src("Radios", "NetworkIdentityInfo.cs");
            Assert.Contains("rig.NetworkReportFor(serial)", text);
            Assert.DoesNotContain("LastNetworkReportFor(", text);
        }

        [Fact]
        public void No_product_code_explains_a_radio_with_the_newest_test_of_any_radio()
        {
            // Only the session and its runner, which keep the per-session
            // cache, may still name it.
            var allowed = new[] { "IWanSessionOwner.cs", "WanSessionOwner.cs", "NetworkTestRunner.cs" };
            foreach (var dir in new[] { "Radios", "JJFlexWpf" })
            foreach (var file in Directory.GetFiles(Path.Combine(IntegrationPassTree.Root, dir), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                    || allowed.Contains(Path.GetFileName(file)))
                    continue;
                string text = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(text, @"\.MostRecentNetworkReport\b"),
                    Path.GetFileName(file) + " reads the newest test of any radio");
            }
        }

        // ── The Settings dialog, at the source ──────────────────────────

        [Fact]
        public void Both_test_buttons_run_one_flow_and_it_gates_before_warning_and_warns_before_testing()
        {
            string net = Src("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs");
            string setup = Src("JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs");
            Assert.Contains("await RunNetworkTestFlow(", Body(net, "private async void TestNetworkButton_Click("));
            Assert.Contains("await RunNetworkTestFlow(", Body(setup, "private async void SetupTestNetworkButton_Click("));

            string flow = Body(net, "Task<Radios.SmartLink.NetworkDiagnosticReport?> RunNetworkTestFlow(");
            int gate = flow.IndexOf("NetworkTestGate.Decide(", StringComparison.Ordinal);
            int warn = flow.IndexOf("ConfirmNetworkTestOnPunchedSession()", StringComparison.Ordinal);
            int test = flow.IndexOf("_rig.RunNetworkTestAsync(", StringComparison.Ordinal);
            Assert.True(gate > 0 && warn > gate && test > warn,
                "the flow must decide the gate, then put the punched-session warning, then test");
            // The warning itself still reads the method actually used (Track A).
            Assert.Contains("!_rig.ConnectedByHolePunch)", Body(net, "private bool ConfirmNetworkTestOnPunchedSession()"));
        }

        [Fact]
        public void No_settings_page_reads_another_radios_report_or_tests_through_the_active_session()
        {
            foreach (var name in new[] { "SettingsDialog.xaml.cs", "SettingsDialog.RadioSetup.cs" })
            {
                string text = Src("JJFlexWpf", "Dialogs", name);
                Assert.DoesNotContain(".MostRecentNetworkReport", text);
                Assert.DoesNotContain("RunNetworkDiagnosticAsync(", text);
                Assert.DoesNotContain("ActiveSession", text);
            }
            Assert.Contains("_rig.NetworkReportFor(_rig.DiagnosticRadioSerial)", Src("JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs"));
        }

        [Fact]
        public void The_network_tab_and_radio_setup_show_one_text_from_one_method()
        {
            string net = Src("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs");
            string refresh = Body(net, "private void RefreshNetworkDiagnosticSections()");
            Assert.Contains("NetworkDiagnosticFor(serial).ToText()", refresh);
            Assert.Contains("NetworkDiagnosticSectionsText.Text = text;", refresh);
            Assert.Contains("SetupDiagnosticSectionsText.Text = text;", refresh);
            Assert.Contains("RefreshNetworkDiagnosticSections();", Body(net, "private void RefreshNetworkTabFromRig()"));
            Assert.Contains("RefreshNetworkDiagnosticSections();", Src("JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs"));

            string xaml = Src("JJFlexWpf", "Dialogs", "SettingsDialog.xaml");
            foreach (var box in new[] { "NetworkDiagnosticSectionsText", "SetupDiagnosticSectionsText" })
            {
                int at = xaml.IndexOf("x:Name=\"" + box + "\"", StringComparison.Ordinal);
                Assert.True(at > 0, box + " is missing");
                string element = xaml.Substring(at, xaml.IndexOf("/>", at, StringComparison.Ordinal) - at);
                Assert.Contains("IsReadOnly=\"True\"", element);
                Assert.Contains("AutomationProperties.Name=", element);
            }
        }

        [Fact]
        public void Copy_and_save_export_this_radios_three_parts()
        {
            string net = Src("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs");
            Assert.Contains(".NetworkDiagnosticFor(serial).ToMarkdown(serial, writtenUtc)", Body(net, "private string? DiagnosticExport(out DateTime writtenUtc)"));
            Assert.Contains("DiagnosticExport(", Body(net, "private void CopyNetworkReportButton_Click("));
            string save = Body(net, "private void SaveNetworkReportButton_Click(");
            Assert.Contains("DiagnosticExport(", save);
            Assert.Contains("File.WriteAllText(dlg.FileName, markdown)", save);
            Assert.DoesNotContain("report.ToMarkdown()", net);
        }

        [Fact]
        public void The_dialog_follows_the_record_through_the_follower_on_its_own_thread()
        {
            string ensure = Body(Src("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"), "private void EnsureDiagnosticFollower()");
            Assert.Contains("new Radios.SmartLink.ConnectRecordFollower(", ensure);
            Assert.Contains("Dispatcher.BeginInvoke(run)", ensure);
            Assert.Contains("_diagnosticFollower?.Dispose()", ensure);
            Assert.DoesNotContain("SmartLinkConnectRecords.Changed +=", Src("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));
        }
    }
}
