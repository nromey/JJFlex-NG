using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #879: the keyer speed's Settings field (ruled by Noel 2026-11-09 20:36
    /// and 2026-11-11 11:44). A setting is an intent: with no radio it saves
    /// into the operator's own keyer file, keeping the rest of their keyer
    /// setup; while connected it goes through the rig, which writes the radio
    /// only when the radio is the operator's own (#686).
    /// </summary>
    /// <remarks>
    /// The connected cases drive the REAL <see cref="FlexBase"/> on the vendored
    /// Radio over its fake transport (<see cref="RigOnVendorRadio"/>): the queue
    /// is parked and run by hand, and what reached the radio is read from the
    /// transport's commands ("cw wpm N", FlexLib's own CWSpeed write). Ownership
    /// is declared in this class's private settings tree. No socket, no radio.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class KeyerSpeedSettingTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(KeyerSpeedSettingTests));
        private const string Serial = "0879-0686-8600-0001";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        public void Dispose() => _scope.Dispose();

        private string OperatorDir() => _scope.PathTo("Radios", "TestOperator");

        private static FlexBase.cfg7620 ReadSetup(string dir)
        {
            using var s = File.OpenRead(Path.Combine(dir, KeyerSpeedStore.FileName));
            return (FlexBase.cfg7620)new XmlSerializer(typeof(FlexBase.cfg7620)).Deserialize(s)!;
        }

        private static void WriteSetup(string dir, FlexBase.cfg7620 cfg)
        {
            Directory.CreateDirectory(dir);
            using var s = File.Open(Path.Combine(dir, KeyerSpeedStore.FileName), FileMode.Create);
            new XmlSerializer(typeof(FlexBase.cfg7620)).Serialize(s, cfg);
        }

        // ── no radio ──

        [Fact]
        public void With_no_radio_the_speed_is_saved_for_the_next_connect_and_the_rest_of_the_keyer_setup_is_kept()
        {
            string dir = OperatorDir();
            WriteSetup(dir, new FlexBase.cfg7620
            {
                BreakinDelay = 450, SidetonePitch = 700, Keyer = FlexBase.IambicValues.iambicB, CWReverse = true, KeyerSpeed = 18,
            });
            Assert.Equal(18, KeyerSpeedStore.Read(null, dir));

            Assert.Equal(KeyerSpeedStore.Outcome.SavedForNextConnect, KeyerSpeedStore.Commit(null, dir, 32));

            var cfg = ReadSetup(dir);
            Assert.Equal(32, cfg.KeyerSpeed);
            Assert.Equal(450, cfg.BreakinDelay);
            Assert.Equal(700, cfg.SidetonePitch);
            Assert.Equal(FlexBase.IambicValues.iambicB, cfg.Keyer);
            Assert.True(cfg.CWReverse);
            Assert.Equal(32, KeyerSpeedStore.Read(null, dir));
        }

        [Fact]
        public void With_nothing_saved_the_field_shows_the_rigs_default_and_a_save_creates_the_file()
        {
            string dir = OperatorDir();
            Assert.Equal(new FlexBase.cfg7620().KeyerSpeed, KeyerSpeedStore.Read(null, dir));
            Assert.Equal(KeyerSpeedStore.Outcome.SavedForNextConnect, KeyerSpeedStore.Commit(null, dir, 25));
            Assert.Equal(25, ReadSetup(dir).KeyerSpeed);
        }

        [Fact]
        public void A_speed_outside_the_radios_range_is_clamped_to_it()
        {
            string dir = OperatorDir();
            KeyerSpeedStore.Commit(null, dir, 400);
            Assert.Equal(KeyerSpeedStore.MaxWpm, ReadSetup(dir).KeyerSpeed);
            KeyerSpeedStore.Commit(null, dir, 1);
            Assert.Equal(KeyerSpeedStore.MinWpm, ReadSetup(dir).KeyerSpeed);
            Assert.Equal(5, KeyerSpeedStore.MinWpm);
            Assert.Equal(100, KeyerSpeedStore.MaxWpm);
        }

        [Fact]
        public void An_unreadable_keyer_file_is_left_alone_rather_than_replaced_for_one_field()
        {
            string dir = OperatorDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, KeyerSpeedStore.FileName);
            File.WriteAllText(path, "not xml");
            Assert.Equal(KeyerSpeedStore.Outcome.NotSaved, KeyerSpeedStore.Commit(null, dir, 30));
            Assert.Equal("not xml", File.ReadAllText(path));
        }

        [Fact]
        public void With_no_operator_folder_nothing_is_saved()
        {
            Assert.Equal(KeyerSpeedStore.Outcome.NotSaved, KeyerSpeedStore.Commit(null, null, 30));
            Assert.Equal(KeyerSpeedStore.Outcome.NotSaved, KeyerSpeedStore.Commit(null, "", 30));
        }

        // ── connected ──

        private sealed class Connected : IDisposable
        {
            public readonly RigOnVendorRadio F;

            public Connected(string operatorParent)
            {
                F = new RigOnVendorRadio(Serial);
                F.Rig.q.MainLoop = true;   // parked: run by hand
                typeof(FlexBase).GetField("_IsConnected", Private)!.SetValue(F.Rig, true);
                // The rig's own operator folder, as OpenParms gives it.
                F.Rig.Callouts.ConfigDirectory = operatorParent;
                F.Rig.Callouts.GetOperatorName = () => "TestOperator";
                // The operator's folder exists by the time a radio is connected.
                Directory.CreateDirectory(Path.Combine(operatorParent, "TestOperator"));
                Assert.True(F.Rig.IsConnected, "positive control: the rig reads as connected");
            }

            public void RunQueue()
            {
                while (F.Rig.q.Count > 0)
                    if (F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
            }

            public int Sent(string fragment) =>
                F.Vendor.Transport.Commands.Count(x => x.Contains(fragment, StringComparison.OrdinalIgnoreCase));

            public void Dispose() => F.Dispose();
        }

        private static void Declare(RadioOwnership ownership) =>
            Assert.True(new RadioConfig { Ownership = ownership }.SaveForRadio(Serial));

        [Fact]
        public void On_the_operators_own_radio_the_speed_is_written_to_the_radio_and_saved()
        {
            Declare(RadioOwnership.Mine);
            using var c = new Connected(_scope.PathTo("Radios"));
            Assert.Equal(KeyerSpeedStore.Outcome.WrittenToRadio, KeyerSpeedStore.Commit(c.F.Rig, OperatorDir(), 33));
            c.RunQueue();
            Assert.Equal(1, c.Sent("cw wpm 33"));
            Assert.Equal(33, ReadSetup(OperatorDir()).KeyerSpeed);
            Assert.Equal(33, c.F.Rig.OperatorKeyerSpeed);
        }

        [Theory]
        [InlineData(RadioOwnership.SomeoneElses)]
        [InlineData(null)]   // never answered: ownership unknown
        public void On_a_radio_that_is_not_the_operators_the_speed_is_saved_for_them_and_the_radio_is_left_alone(RadioOwnership? ownership)
        {
            if (ownership is RadioOwnership o) Declare(o);
            using var c = new Connected(_scope.PathTo("Radios"));
            Assert.Equal(KeyerSpeedStore.Outcome.SavedNotThisRadio, KeyerSpeedStore.Commit(c.F.Rig, OperatorDir(), 33));
            c.RunQueue();
            Assert.Equal(0, c.Sent("cw wpm"));
            Assert.Equal(33, ReadSetup(OperatorDir()).KeyerSpeed);
            Assert.Equal(33, c.F.Rig.OperatorKeyerSpeed);
        }

        // ── the wiring, pinned at its source ──

        private static string Source(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { CaptureMeterSetTests.RepoRoot() }.Concat(parts).ToArray()));

        [Fact]
        public void The_field_is_on_the_Tuning_tab_and_is_validated_before_and_committed_after_everything_else()
        {
            string xaml = Source("JJFlexWpf", "Dialogs", "SettingsDialog.xaml");
            int tuning = xaml.IndexOf("Header=\"Tuning\"", StringComparison.Ordinal);
            int license = xaml.IndexOf("Header=\"License\"", StringComparison.Ordinal);
            int box = xaml.IndexOf("x:Name=\"KeyerSpeedBox\"", StringComparison.Ordinal);
            Assert.True(tuning >= 0 && license > tuning && box > tuning && box < license, "the keyer speed field is not on the Tuning tab");

            string dialog = Source("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs");
            Assert.Contains("RefreshKeyerSpeedFromRig(); }", dialog);   // the rig handed in after construction
            int validate = dialog.IndexOf("if (!ValidateKeyerSpeed()) return false;", StringComparison.Ordinal);
            int firstWrite = dialog.IndexOf("_pttConfig.TimeoutSeconds = timeout;", StringComparison.Ordinal);
            Assert.True(validate > 0 && validate < firstWrite, "the keyer speed is validated after something was written");
            Assert.Contains("CommitKeyerSpeedIfChanged(queued, applied);", dialog);
        }

        [Fact]
        public void The_application_tells_the_store_where_the_operators_folder_is_with_no_radio()
        {
            string globals = Source("globals.vb");
            Assert.Contains("Radios.KeyerSpeedStore.OperatorDirectory =", globals);
            Assert.Contains(@"BaseConfigDir & ""\Radios\"" & currentOperatorName()", globals);
        }

        [Fact]
        public void Connected_the_field_shows_the_operators_value_held_by_the_rig_not_the_file_behind_it()
        {
            Declare(RadioOwnership.SomeoneElses);
            using var c = new Connected(_scope.PathTo("Radios"));
            WriteSetup(OperatorDir(), new FlexBase.cfg7620 { KeyerSpeed = 41 });   // written behind the rig's back
            Assert.Equal(c.F.Rig.OperatorKeyerSpeed, KeyerSpeedStore.Read(c.F.Rig, OperatorDir()));
            Assert.NotEqual(41, KeyerSpeedStore.Read(c.F.Rig, OperatorDir()));
        }
    }
}
