using System;
using System.IO;
using System.Linq;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
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
/// read from source only. The repair-two tests (a Disconnect, then a direct
/// return) need real WPF controls, so they run on a private STA thread: the
/// dialog is an uninitialized object whose constructor never runs, and the
/// controls are never put in a window, asserted by their having no
/// presentation source. Speech is recorded, never rendered (<c>QuietRun</c>).
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

        // Radio > Disconnect: CloseTheRadio clears the shell's radio. It tells
        // this window only that the radio departed (NotifyRigDeparting, repair
        // two, tested below), never NotifyRigGone, so the attachment itself is
        // still the departed radio until something asks it again.
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

    // ---- the still-open window after a menu Disconnect (repair two) ----
    //
    // Codex's B2: Disconnect, then Alt+Tab straight back to the open window.
    // Nothing calls ShowOrFocus, so the reopen repair above never runs. These
    // tests build the window's real parts (its sections, its Mic Gain with its
    // real handler, its poll timer) on a private STA thread, never put in a
    // window, and drive the real departure and the real arrow.

    [Fact]
    public void A_Disconnect_then_a_direct_return_finds_the_radio_controls_unavailable_the_poll_stopped_and_no_gain_confirmed()
    {
        RunOnSta(parts =>
        {
            var (dialog, gain, poll, offAirWatch, rig) = parts;

            // Connected, as SetRig leaves it: available, polling, and the arrow
            // on Mic Gain writes and confirms. The positive control.
            Assert.True(gain.MicGain.IsEnabled);
            Assert.True(poll.IsEnabled);
            Arrow(gain, from: 37);
            Assert.Contains(GainConfirmation(38), ScreenReaderOutput.RecentMessages);

            // Radio > Disconnect: CloseTheRadio disposes the radio, then tells the window.
            rig.Dispose();
            Assert.True(dialog.RigDeparting(rig));

            // Straight back to the window, no ShowOrFocus: nothing to reach.
            Assert.False(gain.MicGain.IsEnabled, "Mic Gain is unavailable once its radio has departed");
            Assert.False(poll.IsEnabled, "the poll stops with the radio");
            Assert.Null(LiveRig(dialog));
            Arrow(gain, from: 38);
            Assert.DoesNotContain(GainConfirmation(39), ScreenReaderOutput.RecentMessages);

            // And the off-air finish is left exactly as it was: the attachment
            // FinishIqAsync compares against is still that radio, and the take's
            // own watch still runs. Nothing about the take ended at the notice.
            Assert.Same(rig, Get(dialog, "_rig"));
            Assert.True(offAirWatch.IsEnabled, "the off-air take's watch keeps running");

            // A second notice, or one for a radio this window does not hold, does nothing.
            Assert.False(dialog.RigDeparting(rig));
            using var other = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            Assert.False(dialog.RigDeparting(other));
        });
    }

    [Fact]
    public void The_next_attachment_is_live_whatever_the_departed_radio_was()
    {
        RunOnSta(parts =>
        {
            var (dialog, _, _, _, rig) = parts;
            Assert.True(dialog.RigDeparting(rig));
            Assert.Null(LiveRig(dialog));
            // A new radio attached is live even before anything clears the mark:
            // the mark names the departed radio, not "departed" in general.
            Put(dialog, "_rig", _live);
            Assert.Same(_live, LiveRig(dialog));
        });
    }

    [Fact]
    public void The_shell_tells_the_window_at_every_dispose_and_the_window_reads_the_live_radio()
    {
        AssertWindowless();
        string vb = Source("globals.vb");
        // At the dispose, while the shell still holds the reference. After it,
        // not before, so CloseTheRadio's opening stays inside the 400-character
        // window KeyDropWithConnectionTests reads for its own ordering.
        string close = Between(vb, "Friend Sub CloseTheRadio()", "End Sub");
        int dispose = close.IndexOf("RigControl.Dispose()", StringComparison.Ordinal);
        int notice = close.IndexOf("JJFlexWpf.Dialogs.CheckYourAudioDialog.NotifyRigDeparting(RigControl)", StringComparison.Ordinal);
        int letGo = close.IndexOf("RigControl = Nothing", StringComparison.Ordinal);
        Assert.True(dispose > 0 && notice > dispose && letGo > notice, "CloseTheRadio tells the window at the dispose");
        string release = Between(vb, "Private Sub ReleaseRigControl(why As String)", "End Sub");
        dispose = release.IndexOf("old.Dispose()", StringComparison.Ordinal);
        notice = release.IndexOf("JJFlexWpf.Dialogs.CheckYourAudioDialog.NotifyRigDeparting(old)", StringComparison.Ordinal);
        letGo = release.IndexOf("RigControl = Nothing", StringComparison.Ordinal);
        Assert.True(dispose > 0 && notice > dispose && letGo > notice, "ReleaseRigControl tells the window at the dispose");

        string src = Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.cs");
        // Mic Gain's handler asks the live radio; the tests above build it the same way.
        Assert.Contains("_gain = new TransmitGainControls(Dispatcher, () => LiveRig,", src, StringComparison.Ordinal);
        Assert.Contains("private void UpdateRadioControlAvailability() => _sections.SetRadioAvailable(LiveRig != null);", src, StringComparison.Ordinal);
        // The departure ends nothing: no SetRig, no take finish, no session end.
        string departing = Between(src, "internal bool RigDeparting(FlexBase rig)", "\n    }");
        foreach (string ends in new[] { "SetRig(", "FinishIqAsync", "_iqRun", "_iqWatch", "_session", "Disarm" })
            Assert.DoesNotContain(ends, departing, StringComparison.Ordinal);
        // Every attachment clears the mark, before availability is worked out.
        string setRig = Between(src, "public void SetRig(FlexBase? rig)", "RefreshOfferedMethods();");
        int assign = setRig.IndexOf("_rig = rig;", StringComparison.Ordinal);
        int clear = setRig.IndexOf("_departedRig = null;", StringComparison.Ordinal);
        int avail = setRig.IndexOf("UpdateRadioControlAvailability();", StringComparison.Ordinal);
        Assert.True(assign > 0 && clear > assign && avail > clear, "SetRig clears the departure mark before availability");
        // Start's door refuses a departed radio as no radio.
        string check = Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs");
        string start = Between(check, "if (LiveRig == null)", "return;");
        Assert.Contains("no_radio_connected", start, StringComparison.Ordinal);
    }

    private const System.Reflection.BindingFlags Private =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    private static FlexBase? LiveRig(CheckYourAudioDialog dialog) =>
        (FlexBase?)typeof(CheckYourAudioDialog).GetProperty("LiveRig", Private)!.GetValue(dialog);

    private static object? Get(object target, string name) =>
        typeof(CheckYourAudioDialog).GetField(name, Private)!.GetValue(target);

    private static void Put(object target, string name, object? value) =>
        typeof(CheckYourAudioDialog).GetField(name, Private)!.SetValue(target, value);

    private static string GainConfirmation(int value) => Lexicon.Get("audio.tx.mic_gain", ("value", value));

    /// <summary>One press of the arrow on Mic Gain: the control's own adjustment path, which raises ValueChanged.</summary>
    private static void Arrow(TransmitGainControls gain, int from)
    {
        gain.MicGain.Value = from;   // the polling path: no event
        typeof(JJFlexWpf.Controls.ValueFieldControl).GetMethod("AdjustValue", Private)!.Invoke(gain.MicGain, new object[] { 1 });
        Assert.Equal(from + 1, gain.MicGain.Value);
    }

    /// <summary>
    /// The window's parts as SetRig leaves them on a connected radio, built on a
    /// private STA thread with no presentation source. The dialog is an
    /// uninitialized object, so its constructor (a Window) never runs; Mic Gain
    /// is a real <see cref="TransmitGainControls"/> whose radio is the window's
    /// own <c>LiveRig</c>, as the constructor wires it.
    /// </summary>
    private static void RunOnSta(Action<(CheckYourAudioDialog dialog, TransmitGainControls gain,
        System.Windows.Threading.DispatcherTimer poll, System.Windows.Threading.DispatcherTimer offAirWatch, FlexBase rig)> body)
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            var verbosity = ScreenReaderOutput.CurrentVerbosity;
            TransmitGainControls? gain = null;
            System.Windows.Threading.DispatcherTimer? poll = null, watch = null;
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            try
            {
                Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
                Lexicon.Load(Lexicon.Partitions);
                ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
                var dialog = (CheckYourAudioDialog)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
                var sections = new DialogSections();
                gain = new TransmitGainControls(System.Windows.Threading.Dispatcher.CurrentDispatcher,
                    () => LiveRig(dialog), () => false, () => null, host: "a test");
                sections.Open(new System.Windows.Controls.StackPanel(), "Levels");
                sections.AddRadio(new System.Windows.Controls.StackPanel(), gain.MicGain);
                poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
                watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
                Put(dialog, "_sections", sections);
                Put(dialog, "_gain", gain);
                Put(dialog, "_pollTimer", poll);
                Put(dialog, "_iqWatch", watch);
                Put(dialog, "_rig", rig);
                typeof(CheckYourAudioDialog).GetMethod("UpdateRadioControlAvailability", Private)!.Invoke(dialog, null);
                poll.Start();
                watch.Start();
                Assert.Null(System.Windows.PresentationSource.FromVisual(gain.MicGain));
                body((dialog, gain, poll, watch, rig));
            }
            catch (System.Reflection.TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
            catch (Exception ex) { failure = ex; }
            finally
            {
                poll?.Stop();
                watch?.Stop();
                gain?.Dispose();
                rig.Dispose();
                ScreenReaderOutput.CurrentVerbosity = verbosity;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }

    private static string Between(string text, string start, string end)
    {
        int at = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + start);
        int stop = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
        Assert.True(stop > at, "no end after: " + start);
        return text.Substring(at, stop - at);
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }
}
