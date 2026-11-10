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

    // ---- the already-open window (repair one): a resolved null detaches ----

    /// <summary>
    /// Stands in for the open window: what it is attached to, and every radio
    /// its SetRig was given. The real SetRig(null) is the departure the window
    /// already owns (it ends any session and stops the poll); what is tested
    /// here is that the reopen GIVES it the null.
    /// </summary>
    private sealed class OpenWindow
    {
        public FlexBase? Attached;
        public readonly System.Collections.Generic.List<FlexBase?> Given = new();
        public void SetRig(FlexBase? rig) { Given.Add(rig); Attached = rig; }
    }

    /// <summary>ShowOrFocus's existing-window branch, in order: resolve, then reattach.</summary>
    private static void Reopen(OpenWindow window, FlexBase? handed, Func<FlexBase?>? source)
    {
        var resolved = CheckYourAudioDialog.RadioToShowOn(handed, source, out _);
        CheckYourAudioDialog.ReattachExisting(window.Attached, resolved, source != null, window.SetRig);
    }

    [Fact]
    public void An_open_window_on_a_departed_radio_lets_it_go_when_the_shell_has_none()
    {
        AssertWindowless();
        var window = new OpenWindow { Attached = _gone };
        // The JJ key and the Audio menu hand the shell's radio, which is null
        // now; Play takes and the Workshop hand the one they captured.
        Reopen(window, null, () => null);
        Assert.Null(window.Attached);
        Assert.Equal(new FlexBase?[] { null }, window.Given);

        var stale = new OpenWindow { Attached = _gone };
        Reopen(stale, _gone, () => null);
        Assert.Null(stale.Attached);
    }

    [Fact]
    public void A_Disconnect_from_the_menu_then_a_reopen_leaves_no_radio_attached_and_a_reconnect_attaches_the_new_one()
    {
        AssertWindowless();
        // As the shell wires it: the source reads the shell's radio when asked.
        FlexBase? shell = _gone;
        Func<FlexBase?> source = () => shell;
        var window = new OpenWindow();

        // Connected, and the window opened on that radio.
        window.SetRig(CheckYourAudioDialog.RadioToShowOn(null, source, out _));
        Assert.Same(_gone, window.Attached);
        window.Given.Clear();

        // Radio > Disconnect: CloseTheRadio clears the shell's radio and tells
        // this window nothing (it never reaches NotifyRigGone). The window is
        // still on the departed radio until something asks it again.
        shell = null;
        Assert.Same(_gone, window.Attached);

        // Reopened through the JJ key or the Audio menu.
        Reopen(window, shell, source);
        Assert.Null(window.Attached);
        Assert.Equal(new FlexBase?[] { null }, window.Given);

        // Reopening again changes nothing more.
        Reopen(window, shell, source);
        Assert.Single(window.Given);

        // A new connection: the shell's ready notice attaches the new radio,
        // and a stale caller handing the old one cannot take it back.
        shell = _live;
        window.SetRig(_live);
        Reopen(window, _gone, source);
        Assert.Same(_live, window.Attached);
    }

    [Fact]
    public void The_same_radio_resolved_again_is_not_set_twice()
    {
        AssertWindowless();
        var window = new OpenWindow { Attached = _live };
        Reopen(window, _live, () => _live);
        Reopen(window, null, () => _live);
        Assert.Empty(window.Given);
    }

    [Fact]
    public void With_no_source_an_open_windows_radio_is_kept_when_none_is_handed()
    {
        AssertWindowless();
        // Before the shell wires the source a null is no decision: as before.
        var window = new OpenWindow { Attached = _live };
        Reopen(window, null, null);
        Assert.Same(_live, window.Attached);
        Assert.Empty(window.Given);
    }

    [Fact]
    public void Both_branches_of_ShowOrFocus_use_the_decision_and_the_open_window_takes_a_null()
    {
        AssertWindowless();
        string src = Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.cs");
        int at = src.IndexOf("public static void ShowOrFocus(FlexBase? rig)", StringComparison.Ordinal);
        Assert.True(at > 0);
        int end = src.IndexOf("_instance.Activate();", at, StringComparison.Ordinal);
        Assert.True(end > at);
        string body = src.Substring(at, end - at);
        int decide = body.IndexOf("rig = RadioToShowOn(rig, RigSource, out string? ignored);", StringComparison.Ordinal);
        int newWindow = body.IndexOf("_instance.SetRig(rig);", StringComparison.Ordinal);
        int existing = body.IndexOf("ReattachExisting(inst._rig, rig, RigSource != null, inst.SetRig);", StringComparison.Ordinal);
        Assert.True(decide > 0 && newWindow > decide && existing > decide, "decided before either branch sets the radio");
        Assert.DoesNotContain("rig ??= RigSource?.Invoke();", body, StringComparison.Ordinal);
        // The conditional that kept a departed radio: a null was never applied.
        Assert.DoesNotContain("if (rig != null && !ReferenceEquals(_instance._rig, rig))", body, StringComparison.Ordinal);
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }
}
