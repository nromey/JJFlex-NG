using System;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

public sealed class LoopbackSliceIdentityTests
{
    private static Slice MakeSlice(VendorRadioFixture vendor, int index, double frequency)
    {
        var slice = vendor.NewSlice(index, 123, frequency, "USB");
        var pan = (Panadapter)Activator.CreateInstance(typeof(Panadapter),
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { vendor.Radio }, null)!;
        typeof(Panadapter).GetProperty(nameof(Panadapter.StreamID))!.GetSetMethod(true)!
            .Invoke(pan, new object[] { (uint)(0x40000000 + index) });
        typeof(Slice).GetProperty(nameof(Slice.Panadapter))!.GetSetMethod(true)!.Invoke(slice, new object[] { pan });
        return slice;
    }

    [Theory]
    [InlineData(1, 3, 0)] // B and D, then A: the shipped positional failure.
    [InlineData(0, 1, 2)] // Ordinary dense roster.
    public void Configures_and_removes_only_the_created_objects(int txIndex, int otherIndex, int newIndex)
    {
        var vendor = new VendorRadioFixture("loopback-identity");
        var tx = MakeSlice(vendor, txIndex, 14.2);
        var other = MakeSlice(vendor, otherIndex, 7.1);
        var before = new[] { tx, other };
        var resources = new LoopbackSliceResources(before, before.Select(s => s.Panadapter));
        var added = MakeSlice(vendor, newIndex, 1.8);
        var roster = before.Append(added).OrderBy(s => s.Index).ToArray();
        var pans = roster.Select(s => s.Panadapter).ToArray();
        Assert.True(resources.Capture(roster, pans));
        Assert.Same(added, resources.Slice);
        Assert.Same(added.Panadapter, resources.Pan);
        Assert.True(resources.Configure(roster, pans, tx, "XVTR"));
        Assert.Equal(tx.Freq, added.Freq);
        Assert.Equal("XVTR", added.RXAnt);
        Assert.Equal(7.1, other.Freq);
        Assert.NotEqual("XVTR", other.RXAnt);
        Assert.NotEqual("XVTR", tx.RXAnt);
        vendor.Transport.Written.Clear();
        Assert.True(resources.Release(roster, pans, tx, tx));
        Assert.Contains($"slice remove {newIndex}", vendor.Transport.Commands);
        Assert.DoesNotContain($"slice remove {txIndex}", vendor.Transport.Commands);
        Assert.DoesNotContain($"slice remove {otherIndex}", vendor.Transport.Commands);
        Assert.Contains($"display pan remove 0x{added.Panadapter.StreamID:X}", vendor.Transport.Commands,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Never_removes_reused_ids_or_the_new_active_slice_and_refuses_ambiguous_creation()
    {
        var vendor = new VendorRadioFixture("loopback-reuse");
        var tx = MakeSlice(vendor, 1, 14.2);
        var added = MakeSlice(vendor, 0, 14.2);
        var resources = new LoopbackSliceResources(new[] { tx }, new[] { tx.Panadapter });
        var roster = new[] { added, tx };
        var pans = roster.Select(s => s.Panadapter).ToArray();
        Assert.True(resources.Capture(roster, pans));
        Assert.False(resources.Release(roster, pans, added, tx));
        var replacement = MakeSlice(vendor, 0, 3.6);
        Assert.True(resources.Release(new[] { replacement, tx }, new[] { replacement.Panadapter, tx.Panadapter }, tx, tx));
        Assert.Empty(vendor.Transport.Commands);
        var ambiguous = new LoopbackSliceResources(new[] { tx }, new[] { tx.Panadapter });
        Assert.False(ambiguous.Capture(new[] { tx, added, replacement }, pans.Append(replacement.Panadapter)));
        Assert.Null(ambiguous.Slice);
    }
}
