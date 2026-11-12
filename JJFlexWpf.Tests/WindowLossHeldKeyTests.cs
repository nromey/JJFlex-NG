using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;
using Keys = System.Windows.Forms.Keys;

namespace JJFlexWpf.Tests;

/// <summary>
/// #900: a held Ctrl+Space that a dialog's window loss ended stays ended
/// until the hold filter confirms a real release. Under JAWS a held key is a
/// stream of down/up pairs about 250 ms apart, so the next synthetic down
/// lands in the next window (Home), and it used to key the radio again as a
/// fresh press; after receive, on the desk microphone. Built on #1020's and
/// #864 7a's notion of a real release: the hold filter's, through
/// MainWindow's app-ended latch, never a second one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven through the production route:</b> the dialog door
/// (<see cref="DialogTransmitKeys"/>), wired by MainWindow's own
/// <c>WireDialogTransmitKeys</c>; Home's step 3 edges
/// (<c>PttKeyDownEdge(PttDoor.Home)</c> and <c>PttKeyUpEdge</c>), which is
/// where the next synthetic down lands; the real <see cref="PttHoldFilter"/>
/// and the real <see cref="PttSafetyController"/>. The filter is clocked by
/// <c>Environment.TickCount64</c>, as in production: a synthetic pair is a
/// down and an up within <see cref="PttHoldFilter.ImplausibleReleaseMs"/>,
/// and a human release comes after a sleep longer than that. A deferral
/// running out is the production timer's Tick, invoked.
/// </para>
/// <para>
/// <b>Desk safety.</b> No window is constructed: MainWindow is
/// <see cref="RuntimeHelpers.GetUninitializedObject"/>, the dialog windows
/// are fakes that raise the two events the door listens to, and every test
/// asserts the thread is MTA, where WPF refuses to build any element. Chirps
/// and push-to-talk speech are off, <see cref="QuietRun"/> has render off, and
/// the rig is an unconnected <see cref="FlexBase"/>.
/// </para>
/// </remarks>
public sealed class WindowLossHeldKeyTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Keys CtrlSpace = Keys.Space | Keys.Control;

    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly PttHoldFilter _filter = new();
    private readonly MainWindow _main;
    private readonly Func<bool>? _endBefore;

    public WindowLossHeldKeyTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        _main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        Put("_pttController", _controller);
        Put("_radioPowerOn", true);
        Put("_pttHoldFilter", _filter);
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

    /// <summary>Home's step 3: Ctrl+Space with focus in the Home fields (not a repeat).</summary>
    private void HomeDown()
    {
        var door = typeof(MainWindow).GetNestedType("PttDoor", BindingFlags.NonPublic);
        Assert.True(door != null, "production enum MainWindow.PttDoor is missing");
        Call("PttKeyDownEdge", Enum.Parse(door!, "Home"));
    }

    /// <summary>Home's PreviewKeyUp for Space.</summary>
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

    /// <summary>A dialog window whose focus loss and close the test raises, as WPF's would.</summary>
    private sealed class FakeWindow : DialogTransmitKeys.IHoldWindow
    {
        public event EventHandler? Deactivated;
        public event EventHandler? Closed;
        public bool HandlesAudioCheckEscape => false;
        public void Lose(bool closes)
        {
            if (closes) Closed?.Invoke(this, EventArgs.Empty);
            else Deactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A held Ctrl+Space as JAWS delivers it, pressed in a dialog, ending on a re-down (no up yet).</summary>
    private void JawsHoldInTheDialog(FakeWindow w)
    {
        Assert.True(DialogTransmitKeys.KeyDown(w, CtrlSpace, false, false));
        Assert.Equal(Hold, _controller.State);
        DialogTransmitKeys.KeyUp(CtrlSpace);           // implausibly fast: a synthetic up
        Assert.True(_filter.SynthesisDetected, "positive control: the filter saw the synthetic signature");
        Assert.True(DialogTransmitKeys.KeyDown(w, CtrlSpace, false, false));   // the re-down claims it
        Assert.Equal(Hold, _controller.State);
        Assert.Same(w, DialogTransmitKeys.HoldWindow);
    }

    // ── JAWS ──

    [Theory]
    [InlineData(true)]    // the dialog closes
    [InlineData(false)]   // the dialog loses focus to another window
    public void A_jaws_hold_a_window_loss_ended_keys_nothing_in_the_next_window_until_really_let_go(bool closes)
    {
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        JawsHoldInTheDialog(w);

        w.Lose(closes);
        Assert.Equal(Idle, _controller.State);         // the window loss still unkeys at once
        Assert.True(Latched, "the hold the window loss ended was not latched");
        Assert.Null(DialogTransmitKeys.HoldWindow);

        // The operator is still holding: the reader's pairs now land in Home.
        for (int i = 0; i < 6; i++)
        {
            HomeUp();
            HomeDown();
            Assert.Equal(Idle, _controller.State);    // the mutation: forget the hold at the loss and this keys
        }

        // They let go: the last up is deferred, and no re-down claims it.
        HomeUp();
        Assert.True(_filter.ReleasePending);
        DeferralRunsOut();
        Assert.False(Latched, "a real release ends the latch");

        // The control: a press after a real release keys as normal.
        HomeDown();
        Assert.Equal(Hold, _controller.State);
    }

    [Fact]
    public void A_window_loss_while_a_release_is_pending_keeps_it_so_the_next_down_is_the_same_hold()
    {
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        JawsHoldInTheDialog(w);
        DialogTransmitKeys.KeyUp(CtrlSpace);           // the reader's next up: deferred
        Assert.True(_filter.ReleasePending);
        Assert.Equal(Hold, _controller.State);

        w.Lose(closes: false);
        Assert.Equal(Idle, _controller.State);
        Assert.True(_filter.ReleasePending, "the window loss threw away the filter's reading of the key");
        Assert.True(Latched);

        HomeDown();                                    // the reader's re-down, in Home
        Assert.Equal(Idle, _controller.State);         // the mutation: reset the filter at the loss and this keys

        // Let go, and the deferral runs out: a release, then a press keys.
        HomeUp();
        DeferralRunsOut();
        Assert.False(Latched);
        HomeDown();
        Assert.Equal(Hold, _controller.State);
    }

    [Fact]
    public void A_jaws_release_made_in_another_program_does_not_refuse_the_next_real_press()
    {
        // The window went to another program, where the reader's pairs and
        // the real release go: nothing reaches us. The pending deferral runs
        // out on its own (no re-down claimed it), which is the release.
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        JawsHoldInTheDialog(w);
        DialogTransmitKeys.KeyUp(CtrlSpace);
        w.Lose(closes: false);
        DeferralRunsOut();
        Assert.False(Latched);
        Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 30);
        HomeDown();
        Assert.Equal(Hold, _controller.State);
    }

    // ── NVDA: a real hold sends nothing more after the loss ──

    [Fact]
    public void An_nvda_hold_a_window_loss_ended_waits_for_the_real_up_and_a_new_press_keys()
    {
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Assert.True(DialogTransmitKeys.KeyDown(w, CtrlSpace, false, false));
        Assert.Equal(Hold, _controller.State);

        w.Lose(closes: true);
        Assert.Equal(Idle, _controller.State);
        Assert.True(Latched);

        // The operator lets go, into Home.
        Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 30);
        HomeUp();
        Assert.False(_filter.SynthesisDetected, "positive control: this is a real hold, as under NVDA");
        Assert.False(Latched, "the real release under NVDA ends the latch at once");
        Assert.Equal(Idle, _controller.State);

        HomeDown();
        Assert.Equal(Hold, _controller.State);
    }

    [Fact]
    public void An_nvda_release_we_never_saw_does_not_refuse_the_next_press()
    {
        // The release went to another program. The next press, wherever it
        // lands, is a fresh press by the filter's reading, and keys.
        AssertTheDeskIsSafe();
        var w = new FakeWindow();
        Assert.True(DialogTransmitKeys.KeyDown(w, CtrlSpace, false, false));
        w.Lose(closes: false);
        Assert.True(Latched);
        Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 30);
        HomeDown();
        Assert.Equal(Hold, _controller.State);
        Assert.False(Latched, "a fresh press ends the latch");
    }

    [Fact]
    public void A_dialogs_window_loss_still_leaves_a_hold_another_door_keyed_alone()
    {
        // The door check of #778c round two is unchanged: the system-wide
        // chord keyed; the dialog's press was a redundant down.
        AssertTheDeskIsSafe();
        Call("HandleSystemWidePttEdge", true);
        var w = new FakeWindow();
        Assert.True(DialogTransmitKeys.KeyDown(w, CtrlSpace, false, false));
        w.Lose(closes: true);
        Assert.Equal(Hold, _controller.State);
        Assert.False(Latched);
        Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 30);
        Call("HandleSystemWidePttEdge", false);
        Assert.Equal(Idle, _controller.State);
    }
}
