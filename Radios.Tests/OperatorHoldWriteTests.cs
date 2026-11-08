using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The change-nothing hold's operator writers (#477 and #976). The
    /// transmit controls and the remote power switch refuse under the hold,
    /// as one step with it, and tell the caller so; the operator writers #976
    /// names that can be one short step are; and the ones that cannot (a
    /// dialog, an upload or a queue between the press and the write) are a
    /// named list, so a NEW two-step operator refusal fails here.
    /// </summary>
    /// <remarks>
    /// <para>The behaviour half runs the real setters on a FlexBase over the
    /// vendor Radio and its fake command transport: no radio, no network.
    /// Import Profiles is pinned from source only, because a run that got past
    /// its refusal would open a file dialog on the desktop.</para>
    /// <para>The pin does not look at <c>GuardSkips</c> (the quiet automatic
    /// skips) or at reads of <c>ChangeNothingActive</c>: only at operator
    /// refusals made with <c>GuardRefuses</c>.</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class OperatorHoldWriteTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(OperatorHoldWriteTests));
        private readonly List<RigOnVendorRadio> _rigs = new();

        public void Dispose()
        {
            foreach (var r in _rigs) r.Dispose();
            _scope.Dispose();
        }

        private RigOnVendorRadio Rig()
        {
            var rig = new RigOnVendorRadio("1111-2222-6300-4444");
            _rigs.Add(rig);
            rig.Rig.SuppressSpeech = true;
            return rig;
        }

        public static IEnumerable<object[]> TxControls() => new[]
        {
            new object[] { "TXReqRCAEnabled", true }, new object[] { "TXReqRCAPolarity", true },
            new object[] { "TXReqACCEnabled", true }, new object[] { "TXReqACCPolarity", true },
            new object[] { "TX1Enabled", true }, new object[] { "TX1Delay", 40 },
            new object[] { "TX2Enabled", true }, new object[] { "TX2Delay", 40 },
            new object[] { "TX3Enabled", true }, new object[] { "TX3Delay", 40 },
            new object[] { "TXACCEnabled", true }, new object[] { "TXACCDelay", 40 },
            new object[] { "HWAlcEnabled", true },
        };

        private static bool Set(FlexBase rig, string name, object value) =>
            (bool)typeof(FlexBase).GetMethod("Set" + name, BindingFlags.Public | BindingFlags.Instance)!
                .Invoke(rig, new[] { value })!;

        private static object Read(FlexBase rig, string name) =>
            typeof(FlexBase).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(rig)!;

        [Theory]
        [MemberData(nameof(TxControls))]
        public void A_transmit_control_is_refused_under_the_hold_and_says_so_to_its_caller(string name, object value)
        {
            var rig = Rig();
            object before = Read(rig.Rig, name);
            int sent = rig.Vendor.Transport.Commands.Count;
            rig.Rig.SetChangeNothingActive(true);

            Assert.False(Set(rig.Rig, name, value));
            Assert.Equal(before, Read(rig.Rig, name));
            Assert.Equal(sent, rig.Vendor.Transport.Commands.Count);
        }

        [Theory]
        [MemberData(nameof(TxControls))]
        public void Control_with_the_hold_off_the_transmit_control_is_written(string name, object value)
        {
            var rig = Rig();
            int sent = rig.Vendor.Transport.Commands.Count;

            Assert.True(Set(rig.Rig, name, value));
            Assert.Equal(value, Read(rig.Rig, name));
            Assert.True(rig.Vendor.Transport.Commands.Count > sent, name + " sent nothing");
        }

        [Fact]
        public void With_no_radio_a_transmit_control_says_it_was_not_written()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            try { Assert.False(rig.SetTX1Enabled(true)); }
            finally { rig.Dispose(); }
        }

        [Fact]
        public void The_transmit_controls_have_no_unguarded_setter_left()
        {
            foreach (var row in TxControls())
            {
                var p = typeof(FlexBase).GetProperty((string)row[0], BindingFlags.Public | BindingFlags.Instance)!;
                Assert.Null(p.SetMethod);
            }
        }

        [Fact]
        public void Remote_power_is_refused_under_the_hold_and_says_so_to_its_caller()
        {
            var rig = Rig();
            bool before = rig.Rig.RemoteOnEnabled;
            rig.Rig.SetChangeNothingActive(true);
            Assert.False(rig.Rig.SetRemoteOnEnabled(!before));
            Assert.Equal(before, rig.Rig.RemoteOnEnabled);

            rig.Rig.SetChangeNothingActive(false);
            Assert.True(rig.Rig.SetRemoteOnEnabled(!before));   // control: the same call with the hold off
            Assert.Equal(!before, rig.Rig.RemoteOnEnabled);
        }

        [Fact]
        public void The_callsign_is_refused_under_the_hold()
        {
            var rig = Rig();
            string before = rig.Rig.RadioCallsign;
            rig.Rig.SetChangeNothingActive(true);
            rig.Rig.RadioCallsign = "K0ABC";
            Assert.Equal(before, rig.Rig.RadioCallsign);

            rig.Rig.SetChangeNothingActive(false);
            rig.Rig.RadioCallsign = "K0ABC";                      // control
            Assert.Equal("K0ABC", rig.Rig.RadioCallsign);
        }

        // ------------------------------------------------------------------
        // A queued operator write asks the hold again at its write (#976,
        // repair one, Codex B1): pressed with the hold off, the hold armed
        // while it waits in the queue, it sends nothing when it runs.
        // ------------------------------------------------------------------

        /// <summary>Hold the queue undrained (the main loop owns it), so the
        /// test decides when the queued item runs.</summary>
        private static void HoldTheQueue(RigOnVendorRadio rig) => rig.Rig.q.MainLoop = true;

        /// <summary>Run the next queued item as the main loop would.</summary>
        private static void RunOne(RigOnVendorRadio rig) =>
            ((FlexBase.FunctionDel)rig.Rig.q.Dequeue().Item)();

        [Theory]
        [InlineData(ProfileTypes.global)]
        [InlineData(ProfileTypes.display)]
        public void A_profile_load_queued_before_the_hold_sends_nothing_when_the_hold_is_armed_before_it_runs(ProfileTypes type)
        {
            var rig = Rig();
            HoldTheQueue(rig);
            int sent = rig.Vendor.Transport.Commands.Count;

            Assert.Equal(GuardedOutcome.Done, rig.Rig.SelectProfileGuarded(new Profile_t("hold-load", type, false)));
            Assert.Equal(1, rig.Rig.q.Count);
            rig.Rig.SetChangeNothingActive(true);
            RunOne(rig);

            Assert.Equal(sent, rig.Vendor.Transport.Commands.Count);
        }

        [Theory]
        [InlineData(ProfileTypes.global)]
        [InlineData(ProfileTypes.display)]
        public void Control_a_queued_profile_load_with_the_hold_off_goes_out_when_it_runs(ProfileTypes type)
        {
            var rig = Rig();
            HoldTheQueue(rig);
            int sent = rig.Vendor.Transport.Commands.Count;

            Assert.Equal(GuardedOutcome.Done, rig.Rig.SelectProfileGuarded(new Profile_t("hold-load", type, false)));
            RunOne(rig);

            Assert.Contains(rig.Vendor.Transport.Commands.Skip(sent), c => c.Contains("load") && c.Contains("hold-load"));
        }

        [Fact]
        public void A_global_profile_save_queued_before_the_hold_sends_nothing_when_the_hold_is_armed_before_it_runs()
        {
            Assert.True(new RadioConfig { Ownership = RadioOwnership.Mine }.SaveForRadio("1111-2222-6300-4444"));
            var rig = Rig();
            HoldTheQueue(rig);
            int sent = rig.Vendor.Transport.Commands.Count;

            Assert.Equal(GuardedOutcome.Done, rig.Rig.SaveProfileGuarded(new Profile_t("hold-save", ProfileTypes.global, false)));
            Assert.Equal(1, rig.Rig.q.Count);
            rig.Rig.SetChangeNothingActive(true);
            RunOne(rig);
            Assert.Equal(sent, rig.Vendor.Transport.Commands.Count);

            // Control: the same save, the hold lifted, goes out.
            rig.Rig.SetChangeNothingActive(false);
            Assert.Equal(GuardedOutcome.Done, rig.Rig.SaveProfileGuarded(new Profile_t("hold-save", ProfileTypes.global, false)));
            RunOne(rig);
            Assert.Contains(rig.Vendor.Transport.Commands.Skip(sent), c => c.Contains("save") && c.Contains("hold-save"));
        }

        /// <summary>
        /// The queued profile writes ask the hold at the write, one step with
        /// it, inside the delegate and not only before the enqueue. Delete
        /// waits for its own queued run on the caller's thread, so it is
        /// pinned here rather than driven.
        /// </summary>
        [Theory]
        [InlineData("theRadio.ProfileDisplaySelection = prof.Name; return true; }))")]
        [InlineData("theRadio.ProfileGlobalSelection = prof.Name; return true; }))")]
        [InlineData("using (OurPowerWrite()) theRadio.ProfileTXSelection = prof.Name;")]
        [InlineData("theRadio.ProfileMICSelection = prof.Name;")]
        [InlineData("theRadio.ProfileMICSelection = name; return true; }))")]
        [InlineData("using (OwnProfileWrite()) notSent = SendGlobalProfileSave(p.Name); return true; }))")]
        [InlineData("theRadio.DeleteGlobalProfile(prof.Name); return true; }))")]
        [InlineData("theRadio.DeleteTXProfile(prof.Name); return true; }))")]
        [InlineData("theRadio.DeleteMICProfile(prof.Name); return true; }))")]
        public void Every_queued_profile_write_asks_the_hold_at_the_write(string write)
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.Profiles.cs"));
            int at = src.IndexOf(write, StringComparison.Ordinal);
            Assert.True(at >= 0, "not found: " + write);
            int gate = src.LastIndexOf("WriteUnlessHeld(", at, StringComparison.Ordinal);
            Assert.True(gate >= 0 && at - gate < 260, "the write is not inside WriteUnlessHeld: " + write);
        }

        // ------------------------------------------------------------------
        // The shape: which operator refusals are still two steps, and why
        // ------------------------------------------------------------------

        /// <summary>
        /// Every operator refusal still made with <c>GuardRefuses</c> (check,
        /// then act) is one that cannot be a single step with the hold, or one
        /// #976 did not cover, and is named here with its reason. A new one
        /// fails until it is moved onto <c>OperatorWriteUnlessHeld</c> or added
        /// here with a reason.
        /// </summary>
        private static readonly Dictionary<(string File, string Key), string> TwoStepRefusals = new()
        {
            // A dialog, an archive on disk and a queued upload come first; asked
            // again at the queued run (ImportProfile).
            [("FlexBase.cs", "settings.guard.action.profile_import")] = "import: refused before the file dialog",
            [("FlexBase.MainThread.cs", "settings.guard.action.profile_import")] = "import: asked again when the queued import runs",
            // An upload; the preflight refuses first.
            [("FlexBase.Firmware.cs", "settings.guard.action.firmware")] = "firmware upload",
            // Queued profile writes: refused up front, before the enqueue, so
            // the press hears it at once; and asked AGAIN at the write, one
            // step with the hold, inside the queued delegate (repair one,
            // pinned by Every_queued_profile_write_asks_the_hold_at_the_write).
            [("FlexBase.Profiles.cs", "settings.guard.action.profile_load")] = "up front; one step again at the queued write",
            [("FlexBase.Profiles.cs", "settings.guard.action.profile_save")] = "up front; one step again at the queued write",
            [("FlexBase.Profiles.cs", "settings.guard.action.profile_delete")] = "up front; one step again at the queued write",
            // Not queued: refused up front, then the send is one step with the
            // hold (WriteUnlessHeld); the confirmation wait is outside it.
            [("FlexBase.Profiles.cs", "settings.guard.action.autosave")] = "up front; one step again at the send",
            // Queued on the IQ queue and confirmed by reply.
            [("FlexBase.FullDuplex.cs", "settings.guard.action.full_duplex")] = "full duplex, queued and confirmed",
            // Queued; the run asks again under the mic lock.
            [("FlexBase.RemoteAudio.cs", "settings.guard.action.mic_input_pc")] = "mic input, queued; asked again at the run",
            // Excluded from #976 in this batch: its file is being rewritten elsewhere.
            [("FlexBase.Listenback.cs", "settings.guard.action.iq_take")] = "IQ take, after the takes merge",
            // Operator refusals #976 did not name; left as they were.
            [("FlexBase.cs", "settings.guard.action.ports")] = "not in #976's list",
            [("FlexBase.cs", "settings.guard.action.front_panel")] = "not in #976's list",
            [("FlexBase.StaticIp.cs", "settings.guard.action.network")] = "not in #976's list",
            [("FlexBase.StaticIp.cs", "settings.guard.action.private_ip")] = "not in #976's list",
            // Refused up front before the token; the command itself is one step (WriteUnlessHeld).
            [("FlexBase.SmartLink.cs", "settings.guard.action.registration")] = "up front, before the token",
        };

        [Fact]
        public void No_new_two_step_operator_refusal()
        {
            var found = new HashSet<(string, string)>();
            foreach (string path in Directory.GetFiles(Path.Combine(RepoRoot(), "Radios"), "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                    || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(path), "GuardRefuses\\(\"([^\"]+)\"\\)"))
                    found.Add((Path.GetFileName(path), m.Groups[1].Value));
            }
            Assert.True(found.Count >= 10, "the scan found " + found.Count + " GuardRefuses calls; it is not reading the tree");

            var unnamed = found.Where(f => !TwoStepRefusals.ContainsKey(f)).ToList();
            Assert.True(unnamed.Count == 0,
                "a two-step operator refusal (GuardRefuses, then the write) that is not on the list: "
                + string.Join(", ", unnamed.Select(u => u.Item1 + " " + u.Item2))
                + ". Make the write one step with OperatorWriteUnlessHeld, or name it here with why it cannot be.");
            var gone = TwoStepRefusals.Keys.Where(k => !found.Contains(k)).ToList();
            Assert.True(gone.Count == 0,
                "named as two-step but no longer found (moved onto one step? take it off the list): "
                + string.Join(", ", gone.Select(g => g.File + " " + g.Key)));
        }

        /// <summary>The operator writers #976 named that can be one short step, are.</summary>
        [Theory]
        [InlineData("FlexBase.cs", "settings.guard.action.rename")]
        [InlineData("FlexBase.cs", "settings.guard.action.callsign")]
        [InlineData("FlexBase.cs", "settings.guard.action.rem_on")]
        [InlineData("FlexBase.Gps.cs", "settings.guard.action.oscillator")]
        [InlineData("FlexBase.TxControls.cs", "settings.guard.action.tx_controls")]
        public void The_one_step_operator_writers_use_the_one_step_helper(string file, string key)
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", file));
            Assert.Contains("OperatorWriteUnlessHeld(\"" + key + "\"", src);
        }

        [Fact]
        public void The_one_step_helper_checks_and_writes_under_the_holds_lock_and_speaks_outside_it()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.cs"));
            int at = src.IndexOf("private bool OperatorWriteUnlessHeld(", StringComparison.Ordinal);
            Assert.True(at >= 0);
            string body = src.Substring(at, src.IndexOf("\n        }", at, StringComparison.Ordinal) - at);
            Assert.Contains("if (WriteUnlessHeld(() => { write(); return true; })) return true;", body);
            Assert.Contains("SayTheHoldRefused(actionKey);", body);
            int wuh = src.IndexOf("private bool WriteUnlessHeld(", StringComparison.Ordinal);
            Assert.Contains("lock (_changeNothingGate)", src.Substring(wuh, 300));   // positive control
        }

        [Fact]
        public void Import_profiles_is_refused_before_its_file_dialog_and_at_its_queued_run()
        {
            string fb = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.cs"));
            int at = fb.IndexOf("public GuardedOutcome ImportProfileDatabase()", StringComparison.Ordinal);
            Assert.True(at >= 0, "ImportProfileDatabase no longer reports a refusal apart from a failure");
            string body = fb.Substring(at, 400);
            int guard = body.IndexOf("GuardRefuses(\"settings.guard.action.profile_import\")", StringComparison.Ordinal);
            int dialog = body.IndexOf("new FlexDB(this)", StringComparison.Ordinal);
            Assert.True(guard > 0 && dialog > guard, "the refusal must come before the file dialog");

            string main = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.MainThread.cs"));
            int run = main.IndexOf("internal void ImportProfile(string name)", StringComparison.Ordinal);
            Assert.True(run >= 0, "ImportProfile moved");
            string runBody = main.Substring(run, Math.Min(1500, main.Length - run));
            int again = runBody.IndexOf("GuardRefuses(\"settings.guard.action.profile_import\")", StringComparison.Ordinal);
            int upload = runBody.IndexOf("SendDBImportFile", StringComparison.Ordinal);
            Assert.True(again > 0 && upload > again, "the queued import must ask again before it powers off and uploads");
        }

        /// <summary>
        /// The TX Controls window, from source (it is a WPF window, and this
        /// project builds none): a refused write puts the control back to the
        /// radio's value and plays no success tone; every setter is asked
        /// whether the radio took it.
        /// </summary>
        [Fact]
        public void A_refused_transmit_control_goes_back_to_the_radios_value_with_no_tone()
        {
            string dlg = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "Dialogs", "TXControlsDialog.xaml.cs"));
            int at = dlg.IndexOf("private void ApplyToggle(CheckBox box, Func<bool>? getter, Func<bool, bool>? setter)", StringComparison.Ordinal);
            Assert.True(at >= 0, "ApplyToggle no longer takes the box, its getter and an answering setter");
            string body = dlg.Substring(at, dlg.IndexOf("\n    }", at, StringComparison.Ordinal) - at);
            int ok = body.IndexOf("if (setter?.Invoke(on) == true)", StringComparison.Ordinal);
            int tone = body.IndexOf("EarconPlayer.ToggleTone(on);", StringComparison.Ordinal);
            int back = body.IndexOf("PutBack(() => box.IsChecked = getter?.Invoke() ?? false);", StringComparison.Ordinal);
            Assert.True(ok >= 0 && tone > ok && back > tone, "the tone must play only when the radio took the write, and a refusal must put the box back");
            Assert.DoesNotContain("Action<bool>? Set", dlg);
            Assert.DoesNotContain("Action<int>? Set", dlg);
            Assert.Contains("return (get?.Invoke() ?? 0).ToString();", dlg);   // a refused delay goes back too
            Assert.Contains("PutBack(() => combo.SelectedIndex = (getter?.Invoke() ?? false) ? 1 : 0);", dlg);

            string main = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs"));
            foreach (var row in TxControls())
                Assert.Contains("dialog.Set" + row[0] + " = v => rig.Set" + row[0] + "(v);", main);
            Assert.Contains("dialog.SetRemoteOnEnabled = v => rig.SetRemoteOnEnabled(v);", main);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }
    }
}
