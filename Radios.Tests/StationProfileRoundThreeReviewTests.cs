using System;
using System.Reflection;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    // Review evidence only. The real queue is held between the ownership
    // decision and dispatch, as it is during ordinary main-loop operation.
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class StationProfileRoundThreeReviewTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(StationProfileRoundThreeReviewTests));
        private const string Serial = "1234-5678-9012-3456";

        public void Dispose() => _scope.Dispose();

        [Theory]
        [InlineData(ProfileTypes.tx, false)]
        [InlineData(ProfileTypes.tx, true)]
        [InlineData(ProfileTypes.mic, false)]
        [InlineData(ProfileTypes.mic, true)]
        public void SelectingAnExistingProfileOnAnotherOwnersRadioMustNotCreateItAtDispatch(
            ProfileTypes type, bool disappearsBeforeDispatch)
        {
            Assert.True(new RadioConfig { Ownership = RadioOwnership.SomeoneElses }.SaveForRadio(Serial));
            using var fixture = new RigOnVendorRadio(Serial);
            string kind = type == ProfileTypes.tx ? "tx" : "mic";
            fixture.Vendor.Status("profile " + kind + " list=owner-profile^");
            using var held = new HeldQueue(fixture.Rig);

            var outcome = fixture.Rig.SelectProfileGuarded(new Profile_t("owner-profile", type, false));
            Assert.Equal(GuardedOutcome.Done, outcome);
            Assert.Equal(1, held.Count);

            if (disappearsBeforeDispatch)
                fixture.Vendor.Status("profile " + kind + " list=");
            held.RunOne();

            Assert.DoesNotContain(fixture.Vendor.Transport.Commands, c => c.Contains(" create "));
        }

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

            public void RunOne()
            {
                object item = _queue.GetType().GetMethod("Dequeue")!.Invoke(_queue, null)!;
                ((Delegate)item.GetType().GetField("Item")!.GetValue(item)!).DynamicInvoke();
            }

            public void Dispose() => _mainLoop.SetValue(_queue, false);
        }
    }
}
