using System;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// Track G review, section 1 step 2: the #495 pre-answer migration
    /// persisted a profile intent while the change-nothing hold was armed.
    /// The coordinator then refused every write, but the protected record
    /// had already changed. These run the PRODUCTION entry
    /// (<c>InitializeStewardshipSession</c>) on a real FlexBase with a
    /// reflection-built Radio, against an isolated settings tree, because the
    /// pure planner's hold test starts after the migration and cannot see
    /// this.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class StewardshipMigrationUnderHoldTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(StewardshipMigrationUnderHoldTests));
        private readonly System.Collections.Generic.List<FlexBase> _rigs = new();

        public void Dispose()
        {
            foreach (var rig in _rigs)
            {
                TheRadioField.SetValue(rig, null); // never FlexBase's to disconnect
                try { rig.Dispose(); } catch { }
            }
            _scope.Dispose();
        }

        private static readonly FieldInfo TheRadioField =
            typeof(FlexBase).GetField("theRadio", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static Radio NewRadio(string serial)
        {
            var radio = (Radio)Activator.CreateInstance(typeof(Radio),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args: new object[] { true }, culture: null)!;
            typeof(Radio).GetProperty(nameof(Radio.Serial))!.GetSetMethod(nonPublic: true)!
                .Invoke(radio, new object[] { serial });
            return radio;
        }

        private static int _serialCounter;

        /// <summary>A well-formed Flex serial (four groups of four digits,
        /// which SaveForRadio insists on) unique within the process.</summary>
        private static string UniqueSerial()
        {
            int n = System.Threading.Interlocked.Increment(ref _serialCounter);
            return "9999-" + (1000 + Environment.ProcessId % 9000).ToString("D4") + "-" + (1000 + n).ToString("D4") + "-7236";
        }

        /// <summary>A radio declared Mine, connected before, question not
        /// answered: exactly the record the migration pre-answers.</summary>
        private (FlexBase rig, Radio radio, string serial) KnownOwnedRadio()
        {
            string serial = UniqueSerial();
            Assert.True(RadioConfig.RecordOwnership(serial, RadioOwnership.Mine));
            ConnectionHistory.Record(serial, "lan", "connected", 1200);
            Assert.Equal(ProfileGuestIntent.NotAnswered, RadioConfig.ProfileIntentOf(serial));

            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests", StationName = "K5TEST" });
            _rigs.Add(rig);
            var radio = NewRadio(serial);
            TheRadioField.SetValue(rig, radio);
            return (rig, radio, serial);
        }

        [Fact]
        public void WithoutTheHold_TheKnownRadioIsPreAnswered()
        {
            // The positive control: the migration still happens.
            var (rig, radio, serial) = KnownOwnedRadio();
            rig.BeginStationAttempt(radio, "test");

            rig.InitializeStewardshipSession();

            Assert.Equal(ProfileGuestIntent.LoadMineAndPutBack, RadioConfig.ProfileIntentOf(serial));
        }

        [Fact]
        public void UnderTheHold_ThePersistedIntentStaysNotAnswered()
        {
            var (rig, radio, serial) = KnownOwnedRadio();
            rig.SetChangeNothingActive(true);
            rig.BeginStationAttempt(radio, "test");

            rig.InitializeStewardshipSession();

            Assert.Equal(ProfileGuestIntent.NotAnswered, RadioConfig.ProfileIntentOf(serial));
        }

        [Fact]
        public void LiftingOnlyTheHold_PermitsTheMigrationOnTheNextAttempt()
        {
            var (rig, radio, serial) = KnownOwnedRadio();
            rig.SetChangeNothingActive(true);
            rig.BeginStationAttempt(radio, "held connect");
            rig.InitializeStewardshipSession();
            Assert.Equal(ProfileGuestIntent.NotAnswered, RadioConfig.ProfileIntentOf(serial));

            rig.SetChangeNothingActive(false);
            rig.BeginStationAttempt(radio, "connect with the hold lifted");
            rig.InitializeStewardshipSession();

            Assert.Equal(ProfileGuestIntent.LoadMineAndPutBack, RadioConfig.ProfileIntentOf(serial));
        }

        [Fact]
        public void TheSessionIsInitialisedOncePerAttempt_EvenUnderTheHold()
        {
            // "Preserve one-time record initialization": the hold skips the
            // migration, not the initialisation.
            var (rig, radio, _) = KnownOwnedRadio();
            rig.SetChangeNothingActive(true);
            rig.BeginStationAttempt(radio, "test");
            rig.InitializeStewardshipSession();
            Assert.True(rig.StewardshipSessionInitialisedForCurrentAttempt);
        }

        // ── the pure rule, which the production entry must consult at the moment it persists ──

        [Fact]
        public void ThePreAnswerRule_RefusesUnderTheHold_AndAnswersWithoutIt()
        {
            Assert.Null(ProfileStewardship.PreAnswerForKnownRadio(
                RadioOwnership.Mine, hasConnectedBefore: true, ProfileGuestIntent.NotAnswered, holdArmed: true));
            Assert.Equal(ProfileGuestIntent.LoadMineAndPutBack, ProfileStewardship.PreAnswerForKnownRadio(
                RadioOwnership.Mine, hasConnectedBefore: true, ProfileGuestIntent.NotAnswered, holdArmed: false));
        }
    }
}
