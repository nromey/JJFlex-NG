using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Junkbox #777, round four: the select race Codex found in the third
    /// re-verify (<see cref="StationProfileRoundThreeReviewTests"/> is his
    /// evidence that nothing is CREATED). These pin the other half the fix
    /// owes: what Select RETURNS, and so what the Profiles dialog says. A
    /// guest's Select of a transmit or microphone profile whose name has gone
    /// from the radio's list by the time the main loop runs it is Refused,
    /// with the ownership refusal sentence for that kind's create, and sends
    /// nothing at all; an unchanged list still selects; an owner neither waits
    /// nor is refused, exactly as before. The real queue is held and drained
    /// by hand, the way the main loop would run it, on the vendored Radio and
    /// its fake transport. No socket, no window, no radio; speech is
    /// suppressed by the fixture and read back through
    /// <c>FlexBase.LastGlobalProfileWriteRefusal</c>.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class StationProfileSelectRaceTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(StationProfileSelectRaceTests));
        private const string Serial = "1234-5678-9012-3456";
        private const string Name = "owner-profile";

        public void Dispose() => _scope.Dispose();

        private static void Declare(RadioOwnership ownership) =>
            Assert.True(new RadioConfig { Ownership = ownership }.SaveForRadio(Serial));

        private static string Kind(ProfileTypes type) => type == ProfileTypes.tx ? "tx" : "mic";

        [Theory]
        [InlineData(RadioOwnership.SomeoneElses, ProfileTypes.tx, "did not create a transmit profile on it")]
        [InlineData(RadioOwnership.Unset, ProfileTypes.tx, "did not create a transmit profile on it")]
        [InlineData(RadioOwnership.SomeoneElses, ProfileTypes.mic, "did not create a microphone profile on it")]
        [InlineData(RadioOwnership.Unset, ProfileTypes.mic, "did not create a microphone profile on it")]
        public void AGuestsSelectWhoseNameIsGoneAtDispatchIsRefusedAndSendsNothing(
            RadioOwnership ownership, ProfileTypes type, string verb)
        {
            Declare(ownership);
            using var fixture = new RigOnVendorRadio(Serial);
            fixture.Vendor.Status("profile " + Kind(type) + " list=" + Name + "^");
            using var held = new HeldQueue(fixture.Rig);

            var select = Task.Run(() => fixture.Rig.SelectProfileGuarded(new Profile_t(Name, type, false)));
            held.WaitForOne();
            fixture.Vendor.Status("profile " + Kind(type) + " list=");
            held.RunOne();
            var outcome = select.GetAwaiter().GetResult();

            // Refused, so the dialog says nothing more: the refusal has spoken.
            Assert.Equal(GuardedOutcome.Refused, outcome);
            Assert.DoesNotContain(fixture.Vendor.Transport.Commands, c => c.Contains(Name));
            string heard = fixture.Rig.LastGlobalProfileWriteRefusal;
            string key = ownership == RadioOwnership.SomeoneElses
                ? "settings.guard.profile_write_not_our_radio"
                : "settings.guard.profile_write_ownership_unknown";
            Assert.Equal(Lexicon.Get(key, ("action", Lexicon.Get(FlexBase.ProfileWriteActionKey(type, delete: false)))), heard);
            Assert.Contains(verb, heard);
        }

        [Theory]
        [InlineData(ProfileTypes.tx)]
        [InlineData(ProfileTypes.mic)]
        public void AGuestsSelectWhoseNameIsStillThereSelectsIt(ProfileTypes type)
        {
            Declare(RadioOwnership.SomeoneElses);
            using var fixture = new RigOnVendorRadio(Serial);
            fixture.Vendor.Status("profile " + Kind(type) + " list=" + Name + "^");
            using var held = new HeldQueue(fixture.Rig);

            var select = Task.Run(() => fixture.Rig.SelectProfileGuarded(new Profile_t(Name, type, false)));
            held.WaitForOne();
            held.RunOne();
            var outcome = select.GetAwaiter().GetResult();

            Assert.Equal(GuardedOutcome.Done, outcome);
            Assert.Contains(fixture.Vendor.Transport.Commands, c => c.Contains(Name));
            Assert.DoesNotContain(fixture.Vendor.Transport.Commands, c => c.Contains(" create "));
            Assert.Null(fixture.Rig.LastGlobalProfileWriteRefusal);
        }

        [Theory]
        [InlineData(ProfileTypes.tx, "profile transmit create \"" + Name + "\"")]
        [InlineData(ProfileTypes.mic, "profile mic create \"" + Name + "\"")]
        public void AnOwnersSelectNeitherWaitsNorIsRefusedAndStillCreatesAGoneName(ProfileTypes type, string wire)
        {
            // An owner's behaviour is unchanged: Select returns as soon as it
            // has queued, and a name gone by dispatch is created, as before.
            Declare(RadioOwnership.Mine);
            using var fixture = new RigOnVendorRadio(Serial);
            fixture.Vendor.Status("profile " + Kind(type) + " list=" + Name + "^");
            using var held = new HeldQueue(fixture.Rig);

            var clock = Stopwatch.StartNew();
            var outcome = fixture.Rig.SelectProfileGuarded(new Profile_t(Name, type, false));
            clock.Stop();
            Assert.Equal(GuardedOutcome.Done, outcome);
            Assert.True(clock.ElapsedMilliseconds < 900,
                "an owner's Select must not wait for its delegate; it took " + clock.ElapsedMilliseconds + " ms");
            Assert.Equal(1, held.Count);

            fixture.Vendor.Status("profile " + Kind(type) + " list=");
            held.RunOne();

            Assert.Contains(wire, fixture.Vendor.Transport.Commands);
            Assert.Null(fixture.Rig.LastGlobalProfileWriteRefusal);
        }

        /// <summary>The production queue in main-loop mode, drained by hand.</summary>
        private sealed class HeldQueue : IDisposable
        {
            private readonly object _queue;
            private readonly PropertyInfo _mainLoop;
            private readonly PropertyInfo _count;

            public HeldQueue(FlexBase rig)
            {
                _queue = typeof(FlexBase).GetField("q", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig)!;
                _mainLoop = _queue.GetType().GetProperty("MainLoop")!;
                _count = _queue.GetType().GetProperty("Count")!;
                _mainLoop.SetValue(_queue, true);
            }

            public int Count => (int)_count.GetValue(_queue)!;

            public void WaitForOne()
            {
                long deadline = Environment.TickCount64 + 5000;
                while (Count < 1)
                {
                    Assert.True(Environment.TickCount64 < deadline, "the Select never reached the queue");
                    Thread.Sleep(5);
                }
            }

            public void RunOne()
            {
                object item = _queue.GetType().GetMethod("Dequeue")!.Invoke(_queue, null)!;
                ((Delegate)item.GetType().GetField("Item")!.GetValue(item)!).DynamicInvoke();
            }

            public void Dispose() => _mainLoop.SetValue(_queue, false);
        }
    }
}
