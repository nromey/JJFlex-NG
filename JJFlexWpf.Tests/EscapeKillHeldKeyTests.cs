using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;
using Keys = System.Windows.Forms.Keys;

namespace JJFlexWpf.Tests;

/// <summary>
/// #1089: Escape and the kill switch end a held Ctrl+Space the way a window
/// loss does since #900: the hold is LATCHED as one the app ended, not
/// forgotten, until the hold filter confirms a real release. Under JAWS a held
/// key is a stream of down/up pairs about 250 ms apart, so after Escape the
/// next synthetic down used to key the radio again as a fresh press. Four
/// doors: Home's Escape, the dialog door's Escape, the radio reporting
/// transmit off on its own, and the hard kill.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven through the production route</b> where one can be reached
/// without a window: the dialog door (<see cref="DialogTransmitKeys"/>, wired
/// by MainWindow's own <c>WireDialogTransmitKeys</c>), MainWindow's
/// <c>RadioStoppedTransmitting</c> (the body of the transmit-change handler's
/// off branch), the controller's own <c>KillTransmitNow</c> with
/// <c>HoldEndedByApp</c> wired as MainWindow wires it, and Home's step 3 edges.
/// Home's Escape branch sits in the key handler, which needs a live window, so
/// it is held by its helper, <c>EndTransmitOutsideTheEdges</c>, plus a pin
/// from the source that the branch calls it.
/// </para>
/// <para>
/// <b>Desk safety.</b> No window is constructed: MainWindow is
/// <see cref="RuntimeHelpers.GetUninitializedObject"/>, the dialog window is a
/// fake that raises the two events the door listens to, and every test asserts
/// the thread is MTA, where WPF refuses to build any element. Chirps and
/// push-to-talk speech are off, <see cref="QuietRun"/> has render off, and the
/// rig is an unconnected <see cref="FlexBase"/>.
/// </para>
/// </remarks>
public sealed class EscapeKillHeldKeyTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Keys CtrlSpace = Keys.Space | Keys.Control;

    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly PttHoldFilter _filter = new();
    private readonly MainWindow _main;
    private readonly Func<bool>? _endBefore;

    public EscapeKillHeldKeyTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        _main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        Put("_pttController", _controller);
        Put("_radioPowerOn", true);
        Put("_pttHoldFilter", _filter);
        // As MainWindow wires it where it builds the controller (pinned in HeldKeyLimitTests).
        _controller.HoldEndedByApp = why => Call("LatchAppEndedHold", why);
        _endBefore = CheckYourAudioDialog.EndAppKeyForReference;
        Call("WireDialogTransmitKeys");
    }

    public void Dispose()
    {
        DialogTransmitKeys.HoldWindowLost("test teardown");
        foreach (var name in new[] { "PttEdge", "HoldInFlight", "AppEndedHoldPending", "EscapeUnkey", "ReleaseNow",
                                     "SessionOwnsTransmitter", "WindowHandlesCheckEscape", "CheckEscapeElsewhere" })
            typeof(DialogTransmitKeys).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
        ((DispatcherTimer?)typeof(MainWindow).GetField("_pttDeferTimer", Any)?.GetValue(_main))?.Stop();
        CheckYourAudioDialog.EndAppKeyForReference = _endBefore;
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        _rig.Dispose();
    }

    // ── plumbing, inside this class on purpose ──

    private static void AssertTheDeskIsSafe()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    private void Put(string field, object? value)
    {
        var f = typeof(MainWindow).GetField(field, Any);
        Assert.True(f != null, "production field MainWindow." + field + " is missing");
        f!.SetValue(_main, value);
    }

    private object? Get(string field)
    {
        var f = typeof(MainWindow).GetField(field, Any);
        Assert.True(f != null, "production field MainWindow." + field + " is missing");
        return f!.GetValue(_main);
    }

    private object? Call(string method, params object[] args)
    {
        var m = typeof(MainWindow).GetMethod(method, Any);
        Assert.True(m != null, "production entry MainWindow." + method + " is missing");
        return m!.Invoke(_main, args);
    }

    private static PttSafetyController.PttState Hold => PttSafetyController.PttState.PttHold;
    private static PttSafetyController.PttState Idle => PttSafetyController.PttState.Idle;

    private bool Latched => (bool)Get("_pttHoldEndedByApp")!;

    private void HomeDown()
    {
        var door = typeof(MainWindow).GetNestedType("PttDoor", BindingFlags.NonPublic);
        Assert.True(door != null, "production enum MainWindow.PttDoor is missing");
        Call("PttKeyDownEdge", Enum.Parse(door!, "Home"));
    }

    private void HomeUp() => Call("PttKeyUpEdge");

    private void DeferralRunsOut()
    {
        var timer = (DispatcherTimer?)Get("_pttDeferTimer");
        Assert.True(timer != null, "no deferral timer was armed");
        timer!.Stop();
        var tick = (EventHandler?)typeof(DispatcherTimer).GetField("Tick", Any)?.GetValue(timer);
        Assert.True(tick != null, "the production timer has no Tick handler");
        tick!(timer, EventArgs.Empty);
    }

    private sealed class FakeWindow : DialogTransmitKeys.IHoldWindow
    {
        public event EventHandler? Deactivated;
        public event EventHandler? Closed;
        public bool HandlesAudioCheckEscape => false;
        public void Lose() { Deactivated?.Invoke(this, EventArgs.Empty); Closed?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>A held Ctrl+Space as JAWS delivers it on Home, ending on a re-down (no up yet).</summary>
    private void JawsHoldOnHome()
    {
        HomeDown();
        Assert.Equal(Hold, _controller.State);
        HomeUp();                                       // implausibly fast: a synthetic up
        Assert.True(_filter.SynthesisDetected, "positive control: the filter saw the synthetic signature");
        HomeDown();                                     // the re-down claims it
        Assert.Equal(Hold, _controller.State);
    }

    /// <summary>The same, pressed in a dialog through the dialog door.</summary>
    private void JawsHoldInTheDialog(FakeWindow w)
    {
        Assert.True(DialogTransmitKeys.KeyDown(w, CtrlSpace, false, false));
        Assert.Equal(Hold, _controller.State);
        DialogTransmitKeys.KeyUp(CtrlSpace);
        Assert.True(_filter.SynthesisDetected, "positive control: the filter saw the synthetic signature");
        Assert.True(DialogTransmitKeys.KeyDown(w, CtrlSpace, false, false));
        Assert.Equal(Hold, _controller.State);
    }

    /// <summary>The operator goes on holding: six more of the reader's pairs key nothing; then a real release ends the latch, and a new press keys.</summary>
    private void PairsKeyNothingThenARealReleaseEndsIt(Action up, Action down)
    {
        for (int i = 0; i < 6; i++)
        {
            up();
            down();
            Assert.Equal(Idle, _controller.State);      // the mutation: forget the hold and this keys
        }
        up();
        Assert.True(_filter.ReleasePending);
        DeferralRunsOut();
        Assert.False(Latched, "a real release ends the latch");
        down();                                         // the control: a press after a real release keys
        Assert.Equal(Hold, _controller.State);
    }

    // ── the four doors, under JAWS ──

    [Fact]
    public void Escape_on_Home_during_a_jaws_hold_unkeys_and_the_readers_next_down_keys_nothing()
    {
        AssertTheDeskIsSafe();
        JawsHoldOnHome();
        Call("EndTransmitOutsideTheEdges", "Escape on Home", (Action)(() => _controller.EscapeUnlock()));
        Assert.Equal(Idle, _controller.State);
        Assert.True(Latched, "Escape forgot the hold instead of latching it");
        PairsKeyNothingThenARealReleaseEndsIt(HomeUp, HomeDown);
    }

    [Fact]
    public void Escape_in_a_dialog_during_a_jaws_hold_unkeys_and_the_readers_next_down_keys_nothing()
    {
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        JawsHoldInTheDialog(w);
        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Escape, false, false), "the door must consume Escape while transmitting");
        Assert.Equal(Idle, _controller.State);
        Assert.True(Latched, "the dialog's Escape forgot the hold instead of latching it");
        PairsKeyNothingThenARealReleaseEndsIt(
            () => DialogTransmitKeys.KeyUp(CtrlSpace),
            () => DialogTransmitKeys.KeyDown(w, CtrlSpace, false, false));
    }

    [Fact]
    public void The_radio_stopping_on_its_own_during_a_jaws_hold_latches_it()
    {
        AssertTheDeskIsSafe();
        JawsHoldOnHome();
        Call("RadioStoppedTransmitting");
        Assert.Equal(Idle, _controller.State);
        Assert.True(Latched, "the external off forgot the hold instead of latching it");
        PairsKeyNothingThenARealReleaseEndsIt(HomeUp, HomeDown);
    }

    [Fact]
    public void A_hard_kill_during_a_jaws_hold_latches_it()
    {
        AssertTheDeskIsSafe();
        JawsHoldOnHome();
        _controller.KillTransmitNow("test");
        Assert.Equal(Idle, _controller.State);
        Assert.True(Latched, "the kill left the hold to the key edges instead of latching it");
        PairsKeyNothingThenARealReleaseEndsIt(HomeUp, HomeDown);
    }

    /// <summary>
    /// The reflected-power cut, through the controller's own check: 100 W
    /// forward and 40 W back on one coherent pair, after the warning, with the
    /// cut setting on (the default). The meter fields are set on the bare rig
    /// by name, as the radio's meter callbacks would.
    /// </summary>
    private void ReflectedCutNow()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var (field, value) in new (string, object)[]
                 { ("_PowerDBM", 50f), ("_ReflectedPower", 46.02f), ("_forwardStamp", now), ("_reflectedStamp", now) })
        {
            var f = typeof(FlexBase).GetField(field, Any);
            Assert.True(f != null, "production field FlexBase." + field + " is missing");
            f!.SetValue(_rig, value);
        }
        var warned = typeof(PttSafetyController).GetField("_healthReflectedWarned", Any);
        Assert.True(warned != null, "production field PttSafetyController._healthReflectedWarned is missing");
        warned!.SetValue(_controller, true);
        var check = typeof(PttSafetyController).GetMethod("CheckReflectedPower", Any);
        Assert.True(check != null, "production entry PttSafetyController.CheckReflectedPower is missing");
        check!.Invoke(_controller, new object[] { _rig });
    }

    /// <summary>
    /// The same class at the reflected-power cut (the second check of #1089,
    /// R1): the cut ends a held push to talk, and under JAWS the reader's next
    /// synthetic down was a fresh press, so once the cut's own five-second
    /// hold ran out the radio keyed again into the same fault while the
    /// operator was still holding. The hold is ended here at once rather than
    /// waited out, which is exactly when the defect bit.
    /// </summary>
    [Fact]
    public void A_reflected_power_cut_during_a_jaws_hold_latches_it_and_nothing_keys_once_its_hold_ends()
    {
        AssertTheDeskIsSafe();
        JawsHoldOnHome();
        ReflectedCutNow();
        Assert.Equal(Idle, _controller.State);          // positive control: the cut fired
        Assert.True(_controller.CutHold.Holding, "positive control: the cut began its hold");
        Assert.True(Latched, "the reflected-power cut left the hold to the key edges instead of latching it");

        _controller.CutHold.Abandon("the test ends the hold early", Environment.TickCount64, immediate: true);
        Assert.False(_controller.CutHold.Holding);
        PairsKeyNothingThenARealReleaseEndsIt(HomeUp, HomeDown);
    }

    /// <summary>The kill's carrier report can arrive before the kill's own bookkeeping: the external door then latches, and the kill finds nothing left to do.</summary>
    [Fact]
    public void A_kill_whose_off_report_arrives_first_is_latched_once_by_the_off_door()
    {
        AssertTheDeskIsSafe();
        JawsHoldOnHome();
        Call("RadioStoppedTransmitting");
        _controller.KillTransmitNow("test");            // its bookkeeping now finds the controller idle
        Assert.True(Latched);
        PairsKeyNothingThenARealReleaseEndsIt(HomeUp, HomeDown);
    }

    // ── NVDA: a real hold, a real release ──

    [Theory]
    [InlineData("home")]
    [InlineData("dialog")]
    [InlineData("off")]
    [InlineData("kill")]
    public void Under_nvda_the_real_release_ends_the_latch_at_once_and_the_next_press_keys(string door)
    {
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Action up = HomeUp, down = HomeDown;
        if (door == "dialog")
        {
            Assert.True(DialogTransmitKeys.KeyDown(w, CtrlSpace, false, false));
            up = () => DialogTransmitKeys.KeyUp(CtrlSpace);
            down = () => DialogTransmitKeys.KeyDown(w, CtrlSpace, false, false);
        }
        else HomeDown();
        Assert.Equal(Hold, _controller.State);

        switch (door)
        {
            case "home": Call("EndTransmitOutsideTheEdges", "Escape on Home", (Action)(() => _controller.EscapeUnlock())); break;
            case "dialog": Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Escape, false, false)); break;
            case "off": Call("RadioStoppedTransmitting"); break;
            case "kill": _controller.KillTransmitNow("test"); break;
        }
        Assert.Equal(Idle, _controller.State);
        Assert.True(Latched);

        Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 30);
        up();
        Assert.False(_filter.SynthesisDetected, "positive control: this is a real hold, as under NVDA");
        Assert.False(Latched, "the real release under NVDA ends the latch at once");

        Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 30);
        down();
        Assert.Equal(Hold, _controller.State);
    }

    // ── a lock has no key held: its input state goes as before ──

    [Fact]
    public void Escape_ending_a_lock_latches_nothing()
    {
        AssertTheDeskIsSafe();
        _controller.ToggleLock();
        Assert.True(_controller.IsTransmitting);
        Call("EndTransmitOutsideTheEdges", "Escape on Home", (Action)(() => _controller.EscapeUnlock()));
        Assert.Equal(Idle, _controller.State);
        Assert.False(Latched);
        HomeDown();
        Assert.Equal(Hold, _controller.State);
    }

    // ── Home's Escape branch, from the source ──

    [Fact]
    public void Homes_escape_branch_ends_the_transmission_through_the_latching_helper()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(DialogCatalogRoot(), "JJFlexWpf", "MainWindow.xaml.cs")).Replace("\r\n", "\n");
        var branch = Regex.Match(src, @"if \(rawKey == Key\.Escape && _pttController\.IsTransmitting\)\s*\{(?<body>[\s\S]*?)\n            \}");
        Assert.True(branch.Success, "Home's Escape branch is not where this test reads it");
        string body = branch.Groups["body"].Value;
        Assert.Contains("EndTransmitOutsideTheEdges(\"Escape on Home\", () => _pttController.EscapeUnlock());", body);
        Assert.DoesNotContain("ResetPttInput()", body);

        int dialog = src.IndexOf("internal bool HandleDialogEscapeUnkey()", StringComparison.Ordinal);
        string dialogBody = src.Substring(dialog, src.IndexOf("\n    }", dialog, StringComparison.Ordinal) - dialog);
        Assert.DoesNotContain("ResetPttInput()", dialogBody);

        int change = src.IndexOf("private void TransmitChangeHandler(", StringComparison.Ordinal);
        string changeBody = src.Substring(change, src.IndexOf("\n    }", change, StringComparison.Ordinal) - change);
        Assert.Contains("if (!transmit) RadioStoppedTransmitting();", changeBody);
    }

    private static string DialogCatalogRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "repository root not found above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
