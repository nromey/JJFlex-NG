using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.StationConnect;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

// Review evidence: no socket, UI, real transport, or physical radio.
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class StationProfileRoundTwoReviewTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(StationProfileRoundTwoReviewTests));
    private const string Serial = "1234-5678-9012-3456";
    private const string Name = "owner-station";
    public void Dispose() => _scope.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateSaveMustNotSayDoneWhenTheTransportDropsBeforeDispatch(bool drop)
    {
        Assert.True(new RadioConfig { Ownership = RadioOwnership.Mine }.SaveForRadio(Serial));
        using var fixture = new RigOnVendorRadio(Serial);
        using var queue = new HeldQueue(fixture.Rig);
        var save = Task.Run(() => fixture.Rig.SaveProfileGuarded(
            new Profile_t(Name, ProfileTypes.global, false), immediately: true));
        Assert.True(SpinWait.SpinUntil(() => queue.Count > 0 || save.IsCompleted, 2000));
        Assert.False(save.IsCompleted);
        Assert.Empty(fixture.Vendor.Transport.Commands);
        if (drop) DropTransport(fixture);
        queue.RunOne();
        var outcome = await save;
        Assert.Equal(!drop, fixture.Vendor.Transport.Commands.Contains("profile global save \"" + Name + "\""));
        Assert.Equal(drop ? GuardedOutcome.Failed : GuardedOutcome.Done, outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LayoutSaveMustKeepDirtyAndReturnAnErrorWhenTheTransportDropsBeforeDispatch(bool drop)
    {
        Assert.True(new RadioConfig { Ownership = RadioOwnership.Mine }.SaveForRadio(Serial));
        using var fixture = new RigOnVendorRadio(Serial);
        MakeSaveable(fixture);
        typeof(FlexBase).GetProperty(nameof(FlexBase.OperatorChangedStationThisSession))!
            .GetSetMethod(true)!.Invoke(fixture.Rig, new object[] { true });
        Assert.Null(fixture.Rig.StationLayoutSaveBlocker());
        using var queue = new HeldQueue(fixture.Rig);
        var save = Task.Run(() => fixture.Rig.SaveCurrentStationLayout());
        Assert.True(SpinWait.SpinUntil(() => queue.Count > 0 || save.IsCompleted, 2000));
        Assert.False(save.IsCompleted);
        if (drop) DropTransport(fixture);
        queue.RunOne();
        string error = await save;
        Assert.Equal(!drop, fixture.Vendor.Transport.Commands.Contains("profile global save \"" + Name + "\""));
        Assert.True(drop ? error != null && fixture.Rig.OperatorChangedStationThisSession
                         : error == null && !fixture.Rig.OperatorChangedStationThisSession,
            $"drop={drop}; error={error ?? "<null>"}; dirty={fixture.Rig.OperatorChangedStationThisSession}; commands={fixture.Vendor.Transport.Commands.Count}");
    }

    [Theory]
    [InlineData(RadioOwnership.Unset, false)]
    [InlineData(RadioOwnership.Unset, true)]
    [InlineData(RadioOwnership.SomeoneElses, false)]
    [InlineData(RadioOwnership.SomeoneElses, true)]
    public void RefusalCoversBothNewAndExistingNames(RadioOwnership ownership, bool exists)
    {
        Assert.True(new RadioConfig { Ownership = ownership }.SaveForRadio(Serial));
        using var fixture = new RigOnVendorRadio(Serial);
        fixture.Vendor.Status("profile global list=" + (exists ? Name + "^" : ""));
        Assert.Equal(exists, fixture.Vendor.Radio.ProfileGlobalList.Contains(Name));
        Assert.Equal(GuardedOutcome.Refused, fixture.Rig.SaveProfileGuarded(new Profile_t(Name, ProfileTypes.global, false), true));
        Assert.Empty(fixture.Vendor.Transport.Commands);
        Assert.Contains("did not save a profile on it", fixture.Rig.LastGlobalProfileWriteRefusal);
        Assert.DoesNotContain("replace", fixture.Rig.LastGlobalProfileWriteRefusal);
    }

    [Theory]
    [InlineData(RadioOwnership.Unset)]
    [InlineData(RadioOwnership.SomeoneElses)]
    public void RefusalIsTrueWithNoGlobalProfileLoaded(RadioOwnership ownership)
    {
        Assert.True(new RadioConfig { Ownership = ownership }.SaveForRadio(Serial));
        using var fixture = new RigOnVendorRadio(Serial);
        MakeSaveable(fixture);
        fixture.Vendor.Status("profile global current=");
        Assert.True(string.IsNullOrEmpty(fixture.Rig.CurrentGlobalProfileName));
        string error = fixture.Rig.SaveCurrentStationLayout();
        Assert.Contains("did not save a profile on it", error);
        Assert.DoesNotContain("replace", error);
        Assert.Empty(fixture.Vendor.Transport.Commands);
    }

    private static void DropTransport(RigOnVendorRadio fixture)
    {
        // Same transport-state transition that SendCommand checks. The fixture
        // has no connection event subscribers, representing the send observing
        // the drop before higher-level teardown. Do not call Radio.Disconnect.
        typeof(CommandCommunicationBase).GetMethod("PublishDisconnected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Vendor.Transport, new object[] { fixture.Vendor.Transport.CurrentConnection! });
        Assert.False(fixture.Vendor.Transport.IsConnected);
    }

    private static void MakeSaveable(RigOnVendorRadio fixture)
    {
        typeof(FlexBase).GetField("_IsConnected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Rig, true);
        var tracker = (RosterTracker)typeof(FlexBase).GetProperty("RosterTracker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Rig)!;
        tracker.OwnHandleEstablished(RigOnVendorRadio.OurHandle, fixture.Binding.Generation);
        tracker.ClientAdded(new RosterEntry(RigOnVendorRadio.OurHandle, "client-1", true, "K5TEST", "JJFlexTests"), fixture.Binding.Generation);
        fixture.Vendor.Status("profile global current=" + Name);
    }

    private sealed class HeldQueue : IDisposable
    {
        private readonly object _queue;
        private readonly PropertyInfo _mainLoop;
        public HeldQueue(FlexBase rig)
        {
            _queue = typeof(FlexBase).GetField("q", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig)!;
            _mainLoop = _queue.GetType().GetProperty("MainLoop")!;
            _mainLoop.SetValue(_queue, true);
        }
        public int Count => (int)_queue.GetType().GetProperty("Count")!.GetValue(_queue)!;
        public void RunOne()
        {
            object item = _queue.GetType().GetMethod("Dequeue")!.Invoke(_queue, null)!;
            ((Delegate)item.GetType().GetField("Item")!.GetValue(item)!).DynamicInvoke();
        }
        public void Dispose() => _mainLoop.SetValue(_queue, false);
    }
}
