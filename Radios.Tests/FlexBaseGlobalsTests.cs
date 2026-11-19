using System;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #1160: the sentinel for <see cref="FlexBaseGlobals"/> and
/// <see cref="FlexBaseGlobalsRestored"/>. A test that leaves one of FlexBase's
/// three process-wide statics changed is seen, recorded and put back before
/// the next test runs.
/// </summary>
public sealed class FlexBaseGlobalsTests
{
    private static MethodInfo Me(string name) => typeof(FlexBaseGlobalsTests).GetMethod(name)!;

    [Fact]
    public void The_restore_is_wired_to_every_test_in_the_assembly()
    {
        // Applied at assembly level: it saw THIS test start, which no
        // attribute on this class or method could have arranged.
        Assert.Null(typeof(FlexBaseGlobalsTests).GetCustomAttribute<FlexBaseGlobalsRestored>());
        Assert.Null(Me(nameof(The_restore_is_wired_to_every_test_in_the_assembly)).GetCustomAttribute<FlexBaseGlobalsRestored>());
        Assert.Equal(Me(nameof(The_restore_is_wired_to_every_test_in_the_assembly)), FlexBaseGlobalsRestored.Current);
    }

    [Fact]
    public void A_test_starts_with_the_statics_as_the_assembly_found_them()
    {
        Assert.Empty(FlexBaseGlobals.Pristine.Differences());
    }

    [Fact]
    public void A_planted_leak_is_seen_recorded_and_put_back_when_the_test_ends()
    {
        // The planted leak: all three changed, as a FlexBase constructor would.
        Vita.VitaSocket.TraceSink = _ => { };
        API.ProgramName = "planted leak";
        API.IsGUI = !FlexBaseGlobals.Pristine.IsGUI;
        Assert.Equal(new[] { "Vita.VitaSocket.TraceSink", "API.ProgramName", "API.IsGUI" },
            FlexBaseGlobals.Pristine.Differences());

        var method = Me(nameof(A_planted_leak_is_seen_recorded_and_put_back_when_the_test_ends));
        new FlexBaseGlobalsRestored().After(method);

        Assert.Empty(FlexBaseGlobals.Pristine.Differences());
        Assert.Contains(FlexBaseGlobalsRestored.Changers,
            c => c.StartsWith(nameof(FlexBaseGlobalsTests) + "." + method.Name + ": ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_FlexBase_constructor_changes_the_statics_and_a_capture_puts_them_back()
    {
        var before = FlexBaseGlobals.Capture();
        var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "FlexBaseGlobalsTests" });
        try
        {
            // Positive control: the constructor really does write them.
            Assert.Contains("API.IsGUI", before.Differences());
            Assert.Contains("Vita.VitaSocket.TraceSink", before.Differences());
            Assert.Contains("API.ProgramName", before.Differences());
        }
        finally
        {
            before.Restore();
            try { rig.Dispose(); } catch { }
        }
        Assert.Empty(before.Differences());
    }
}
