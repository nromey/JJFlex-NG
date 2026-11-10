using System;
using System.IO;
using System.Linq;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Check your audio is never switched onto a radio that is gone (#897). Play
/// takes captures its radio when it opens, so after a reconnect its "Add a
/// take to this test" handed the window the old radio, and the window left
/// the live one without its teardown. The window now takes only the shell's
/// current radio, at the one seam every caller uses.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> No window: the decision is a static function, and the
/// radios are unconnected <see cref="FlexBase"/> objects on an MTA thread,
/// asserted in every test. <c>ShowOrFocus</c> itself opens a window and is
/// read from source only.
/// </remarks>
public sealed class CheckWindowCurrentRadioTests : IDisposable
{
    private readonly FlexBase _live = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
    private readonly FlexBase _gone = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

    public void Dispose()
    {
        _live.Dispose();
        _gone.Dispose();
    }

    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    [Fact]
    public void A_radio_that_is_not_the_current_one_is_ignored_for_the_current_one()
    {
        AssertWindowless();
        var chosen = CheckYourAudioDialog.RadioToShowOn(_gone, () => _live, out string? ignored);
        Assert.Same(_live, chosen);
        Assert.Equal("the shell's current radio is another one", ignored);
    }

    [Fact]
    public void A_handed_radio_with_no_current_one_is_ignored()
    {
        AssertWindowless();
        Assert.Null(CheckYourAudioDialog.RadioToShowOn(_gone, () => null, out string? ignored));
        Assert.Equal("the shell has no radio now", ignored);
    }

    [Fact]
    public void The_current_radio_handed_in_is_kept()
    {
        AssertWindowless();
        // Positive control: the ordinary case changes nothing.
        Assert.Same(_live, CheckYourAudioDialog.RadioToShowOn(_live, () => _live, out string? ignored));
        Assert.Null(ignored);
    }

    [Fact]
    public void No_radio_handed_takes_the_current_one_as_before()
    {
        AssertWindowless();
        Assert.Same(_live, CheckYourAudioDialog.RadioToShowOn(null, () => _live, out string? ignored));
        Assert.Null(ignored);
    }

    [Fact]
    public void With_no_source_the_handed_radio_is_kept()
    {
        AssertWindowless();
        // Before the shell wires the source, and in windowless tests.
        Assert.Same(_gone, CheckYourAudioDialog.RadioToShowOn(_gone, null, out string? ignored));
        Assert.Null(ignored);
    }

    [Fact]
    public void Both_branches_of_ShowOrFocus_use_the_decision()
    {
        AssertWindowless();
        string src = Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.cs");
        int at = src.IndexOf("public static void ShowOrFocus(FlexBase? rig)", StringComparison.Ordinal);
        Assert.True(at > 0);
        int decide = src.IndexOf("rig = RadioToShowOn(rig, RigSource, out string? ignored);", at, StringComparison.Ordinal);
        int newWindow = src.IndexOf("_instance.SetRig(rig);", at, StringComparison.Ordinal);
        int switchRig = src.IndexOf("if (rig != null && !ReferenceEquals(_instance._rig, rig)) _instance.SetRig(rig);", at, StringComparison.Ordinal);
        Assert.True(decide > at && newWindow > decide && switchRig > decide, "decided before either branch sets the radio");
        Assert.DoesNotContain("rig ??= RigSource?.Invoke();", src.Substring(at, switchRig - at), StringComparison.Ordinal);
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }
}
