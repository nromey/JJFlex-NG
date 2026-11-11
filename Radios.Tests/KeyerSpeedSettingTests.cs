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

            /// <summary>The current operator, as the application's delegate answers it; change it to switch operators.</summary>
            public string Operator = "TestOperator";

            public Connected(string operatorParent)
            {
                F = new RigOnVendorRadio(Serial);
                F.Rig.q.MainLoop = true;   // parked: run by hand
                typeof(FlexBase).GetField("_IsConnected", Private)!.SetValue(F.Rig, true);
                // The rig's own operator folder, as OpenParms gives it: the
                // application's delegate follows the CURRENT operator.
                F.Rig.Callouts.ConfigDirectory = operatorParent;
                F.Rig.Callouts.GetOperatorName = () => Operator;
                // The operator's folder exists by the time a radio is connected.
                Directory.CreateDirectory(Path.Combine(operatorParent, "TestOperator"));
                // What the connect itself does: read the operator's keyer
                // setup into the rig (the main thread's issue7620(false)).
                Issue7620(false);
                Assert.True(F.Rig.IsConnected, "positive control: the rig reads as connected");
            }

            public void Issue7620(bool write) =>
                typeof(FlexBase).GetMethod("issue7620", Private)!.Invoke(F.Rig, new object[] { write });

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
        [InlineData(RadioOwnership.SomeoneElses, KeyerSpeedStore.Outcome.SavedNotThisRadio)]
        [InlineData(null, KeyerSpeedStore.Outcome.SavedOwnershipUnknown)]   // never answered: ownership unknown
        public void On_a_radio_that_is_not_the_operators_the_speed_is_saved_for_them_and_the_radio_is_left_alone(
            RadioOwnership? ownership, KeyerSpeedStore.Outcome expected)
        {
            if (ownership is RadioOwnership o) Declare(o);
            using var c = new Connected(_scope.PathTo("Radios"));
            Assert.Equal(expected, KeyerSpeedStore.Commit(c.F.Rig, OperatorDir(), 33));
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

        // ── repair one: the operator who holds the setup, and receipts that are true ──

        private string OtherOperatorDir() => _scope.PathTo("Radios", "OtherOperator");

        private static FlexBase.cfg7620 SetupA() => new FlexBase.cfg7620
        {
            KeyerSpeed = 33, BreakinDelay = 300, SidetonePitch = 600, Keyer = FlexBase.IambicValues.off, CWReverse = false,
        };

        private static FlexBase.cfg7620 SetupB() => new FlexBase.cfg7620
        {
            KeyerSpeed = 55, BreakinDelay = 750, SidetonePitch = 900, Keyer = FlexBase.IambicValues.iambicB, CWReverse = true,
        };

        private static void AssertSetup(FlexBase.cfg7620 expected, FlexBase.cfg7620 actual, string whose)
        {
            Assert.True(expected.KeyerSpeed == actual.KeyerSpeed, whose + ": speed " + actual.KeyerSpeed);
            Assert.True(expected.BreakinDelay == actual.BreakinDelay, whose + ": break-in delay " + actual.BreakinDelay);
            Assert.True(expected.SidetonePitch == actual.SidetonePitch, whose + ": sidetone pitch " + actual.SidetonePitch);
            Assert.True(expected.Keyer == actual.Keyer, whose + ": keyer " + actual.Keyer);
            Assert.True(expected.CWReverse == actual.CWReverse, whose + ": reverse paddles " + actual.CWReverse);
        }

        [Fact]
        public void An_operator_switch_while_connected_reads_and_saves_the_new_operators_own_keyer_setup()
        {
            // The 2026-11-11 check's reproduction: connected as A, switch to
            // B through Radio, Operators, then change the speed. The field
            // showed A's speed and the save wrote A's WHOLE setup into B's file.
            Declare(RadioOwnership.Mine);
            WriteSetup(OperatorDir(), SetupA());
            WriteSetup(OtherOperatorDir(), SetupB());
            using var c = new Connected(_scope.PathTo("Radios"));
            Assert.Equal(33, c.F.Rig.OperatorKeyerSpeed);   // positive control: connected as A

            c.Operator = "OtherOperator";
            Assert.Equal(55, KeyerSpeedStore.Read(c.F.Rig, OtherOperatorDir()));
            Assert.Equal(KeyerSpeedStore.Outcome.WrittenToRadio, KeyerSpeedStore.Commit(c.F.Rig, OtherOperatorDir(), 56));
            c.RunQueue();
            Assert.Equal(1, c.Sent("cw wpm 56"));

            var b = SetupB();
            b.KeyerSpeed = 56;
            AssertSetup(b, ReadSetup(OtherOperatorDir()), "B's file");
            AssertSetup(SetupA(), ReadSetup(OperatorDir()), "A's file");
        }

        [Fact]
        public void An_operator_with_no_keyer_file_starts_from_the_defaults_not_the_last_operators()
        {
            Declare(RadioOwnership.SomeoneElses);
            WriteSetup(OperatorDir(), SetupA());
            using var c = new Connected(_scope.PathTo("Radios"));
            Assert.Equal(33, c.F.Rig.OperatorKeyerSpeed);

            c.Operator = "OtherOperator";
            Directory.CreateDirectory(OtherOperatorDir());
            Assert.Equal(KeyerSpeedStore.DefaultWpm, c.F.Rig.OperatorKeyerSpeed);
            Assert.Equal(KeyerSpeedStore.Outcome.SavedNotThisRadio, KeyerSpeedStore.Commit(c.F.Rig, OtherOperatorDir(), 30));

            var expected = new FlexBase.cfg7620 { KeyerSpeed = 30 };
            AssertSetup(expected, ReadSetup(OtherOperatorDir()), "B's new file");
            AssertSetup(SetupA(), ReadSetup(OperatorDir()), "A's file");
        }

        [Fact]
        public void The_last_line_never_writes_one_operators_keyer_setup_into_anothers_folder()
        {
            // Any keyer write (the rig rewrites the whole file on every keyer
            // change) after the operator changed, with nothing having read the
            // new operator's setup first: B's file is not replaced by A's.
            WriteSetup(OperatorDir(), SetupA());
            WriteSetup(OtherOperatorDir(), SetupB());
            using var c = new Connected(_scope.PathTo("Radios"));

            // Positive control: for the operator whose setup it holds, the write happens.
            File.Delete(Path.Combine(OperatorDir(), KeyerSpeedStore.FileName));
            c.Issue7620(true);
            AssertSetup(SetupA(), ReadSetup(OperatorDir()), "A's file, rewritten");

            c.Operator = "OtherOperator";
            c.Issue7620(true);
            AssertSetup(SetupB(), ReadSetup(OtherOperatorDir()), "B's file");
        }

        [Fact]
        public void An_operator_change_reads_the_new_operators_keyer_setup_at_once()
        {
            string rig = Source("Radios", "FlexBase.cs");
            int handler = rig.IndexOf("public void OperatorChangeHandler()", StringComparison.Ordinal);
            int ensure = rig.IndexOf("EnsureKeyerSetupIsCurrentOperators();", handler, StringComparison.Ordinal);
            int end = rig.IndexOf("FilterObj.OperatorChangeHandler();", handler, StringComparison.Ordinal);
            Assert.True(handler > 0 && ensure > handler && ensure < end,
                "an operator change does not bring the rig's keyer setup up to the new operator");
        }

        private static string LexiconText(string key)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(Source("Radios", "Lexicon", "settings.json"));
            return doc.RootElement.GetProperty(key).GetProperty("text").GetString()!;
        }

        [Fact]
        public void Never_answered_ownership_has_its_own_receipt_and_is_never_called_someone_elses()
        {
            string elses = LexiconText("settings.keyer.speed_not_this_radio");
            string unknown = LexiconText("settings.keyer.speed_ownership_unknown");
            Assert.NotEqual(elses, unknown);
            Assert.DoesNotContain("not yours", unknown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("someone else", unknown, StringComparison.OrdinalIgnoreCase);

            string dialog = Source("JJFlexWpf", "Dialogs", "SettingsDialog.KeyerSpeed.cs");
            int unknownCase = dialog.IndexOf("case KeyerSpeedStore.Outcome.SavedOwnershipUnknown:", StringComparison.Ordinal);
            int unknownWords = dialog.IndexOf("settings.keyer.speed_ownership_unknown", StringComparison.Ordinal);
            int nextCase = dialog.IndexOf("default:", unknownCase, StringComparison.Ordinal);
            Assert.True(unknownCase > 0 && unknownWords > unknownCase && unknownWords < nextCase,
                "Settings does not give never-answered ownership its own receipt");
        }

        [Fact]
        public void A_save_with_no_radio_promises_no_connect_the_gate_would_refuse()
        {
            // The restore runs only inside the full automatic-write gate. These
            // are the gate's own answers, unchanged by this field: an owned
            // radio the operator set to be left alone, or with the
            // change-nothing hold armed, gets nothing at connect...
            Assert.False(ProfileStewardship.MayWriteSharedStateAutomatically(new ProfileSituation
            { Ownership = RadioOwnership.Mine, Intent = ProfileGuestIntent.LeaveAlone }));
            Assert.False(ProfileStewardship.MayWriteSharedStateAutomatically(new ProfileSituation
            { Ownership = RadioOwnership.Mine, Intent = ProfileGuestIntent.LoadMineAndPutBack, ChangeNothingArmed = true }));
            // ...and the positive control: loading the operator's settings is what applies it.
            Assert.True(ProfileStewardship.MayWriteSharedStateAutomatically(new ProfileSituation
            { Ownership = RadioOwnership.Mine, Intent = ProfileGuestIntent.LoadMineAndPutBack }));

            // So nothing Settings says may promise the next connect outright
            // (Codex worded them 2026-11-11; any later wording must stay conditional).
            foreach (var key in new[] { "settings.keyer.speed_waits_for_radio", "settings.keyer.speed_help" })
            {
                string text = LexiconText(key);
                Assert.DoesNotContain("next connect", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("when you connect to it", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("set on your own radio when you connect", text, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── repair two: a new operator's first save, with no folder yet ──

        [Theory]
        [InlineData(RadioOwnership.Mine, KeyerSpeedStore.Outcome.WrittenToRadio)]
        [InlineData(RadioOwnership.SomeoneElses, KeyerSpeedStore.Outcome.SavedNotThisRadio)]
        [InlineData(null, KeyerSpeedStore.Outcome.SavedOwnershipUnknown)]
        public void A_new_operators_first_connected_save_makes_their_folder_and_their_file(
            RadioOwnership? ownership, KeyerSpeedStore.Outcome expected)
        {
            // The 2026-11-11 recheck's case: connected as A, an operator made
            // and chosen through Radio, Operators who has never connected has
            // no folder under Radios. The save said it was saved and wrote
            // nothing. This test does NOT make the folder.
            if (ownership is RadioOwnership o) Declare(o);
            WriteSetup(OperatorDir(), SetupA());
            using var c = new Connected(_scope.PathTo("Radios"));
            string fresh = _scope.PathTo("Radios", "FreshOperator");
            Assert.False(Directory.Exists(fresh), "positive control: the new operator has no folder");

            c.Operator = "FreshOperator";
            Assert.Equal(KeyerSpeedStore.DefaultWpm, KeyerSpeedStore.Read(c.F.Rig, fresh));
            Assert.False(Directory.Exists(fresh), "reading the field must write nothing");

            Assert.Equal(expected, KeyerSpeedStore.Commit(c.F.Rig, fresh, 31));
            Assert.True(File.Exists(Path.Combine(fresh, KeyerSpeedStore.FileName)), "the save said it saved, and no file was written");
            AssertSetup(new FlexBase.cfg7620 { KeyerSpeed = 31 }, ReadSetup(fresh), "the new operator's file");
            AssertSetup(SetupA(), ReadSetup(OperatorDir()), "A's file");
        }

        [Theory]
        [InlineData(RadioOwnership.SomeoneElses, 0)]
        [InlineData(RadioOwnership.Mine, 1)]   // the operating edit still reaches their own radio
        public void A_connected_save_that_cannot_be_written_says_it_was_not_saved(RadioOwnership ownership, int sentToRadio)
        {
            // Something in the way of the folder: a FILE where the new
            // operator's folder would go. Nothing can create the folder, so
            // nothing may report the speed as saved.
            Declare(ownership);
            WriteSetup(OperatorDir(), SetupA());
            using var c = new Connected(_scope.PathTo("Radios"));
            string blocked = _scope.PathTo("Radios", "BlockedOperator");
            File.WriteAllText(blocked, "not a folder");

            c.Operator = "BlockedOperator";
            Assert.Equal(KeyerSpeedStore.Outcome.NotSaved, KeyerSpeedStore.Commit(c.F.Rig, blocked, 31));
            c.RunQueue();
            Assert.Equal(sentToRadio, c.Sent("cw wpm 31"));
            Assert.Equal("not a folder", File.ReadAllText(blocked));
        }
    }
}
