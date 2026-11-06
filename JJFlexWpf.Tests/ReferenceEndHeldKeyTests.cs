using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;
using Holder = JJFlexWpf.Dialogs.CheckYourAudioDialog.ReferenceEndHolder;
using Keys = System.Windows.Forms.Keys;

namespace JJFlexWpf.Tests;

/// <summary>
/// Unified takes, round five (7a): when a reference recording ends under a
/// held Ctrl+Space and the app lets go of its own key, the hold stays ended
/// until the operator really lets go. Under JAWS, which delivers a held key as
/// down/up pairs about 250 ms apart, the next synthetic down used to be a new
/// press and keyed the radio again; once the radio had received, that was the
/// desk microphone on the air.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven through the production route end to end:</b> the reference's
/// end is <c>CheckYourAudioDialog.EndTransmissionForReference</c>, holder App,
/// wired to MainWindow's <c>HandleReferenceEndUnkey</c> as MainWindow wires
/// it; the key edges are MainWindow's own (<c>HandleSystemWidePttEdge</c>, and
/// the dialog door for the NVDA case); the hold filter is the real
/// <see cref="PttHoldFilter"/>; the controller is the real
/// <see cref="PttSafetyController"/>. Every assertion that something did or
/// did not key lands on the controller's state.
/// </para>
/// <para>
/// <b>Timing.</b> The filter is clocked by <c>Environment.TickCount64</c>, as
/// in production. A JAWS pair is a down and then an up within
/// <see cref="PttHoldFilter.ImplausibleReleaseMs"/>; a human release is one
/// that comes after a sleep longer than that. A deferral running out is the
/// production timer's own Tick, invoked directly, as
/// <see cref="DialogTransmitKeysTests"/> does: this represents expiry, not a
/// wall-clock wait.
/// </para>
/// <para>
/// <b>Desk safety.</b> No window is constructed: MainWindow and the dialog are
/// <see cref="RuntimeHelpers.GetUninitializedObject"/>, so no constructor runs,
/// no XAML loads and no HWND exists. Every test asserts the thread is MTA,
/// where WPF refuses to build any element. The controller has chirps and
/// speech off, <see cref="QuietRun"/> has render off, and the rig is an
/// unconnected <see cref="FlexBase"/> whose transmit writes go nowhere.
/// </para>
/// </remarks>
public sealed class ReferenceEndHeldKeyTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly PttHoldFilter _filter = new();
    private readonly MainWindow _main;
    private readonly CheckYourAudioDialog _dialog;
    private readonly Func<PttSafetyController?>? _sourceBefore;
    private readonly Func<bool>? _endBefore;

    public ReferenceEndHeldKeyTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });

        _main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        Put("_pttController", _controller);
        Put("_radioPowerOn", true);
        Put("_pttHoldFilter", _filter);

        _dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
        var rigField = typeof(CheckYourAudioDialog).GetField("_rig", Any);
        Assert.True(rigField != null, "production field CheckYourAudioDialog._rig is missing");
        rigField!.SetValue(_dialog, _rig);

        _sourceBefore = AudioWorkshopDialog.PttControllerSource;
        _endBefore = CheckYourAudioDialog.EndAppKeyForReference;
        AudioWorkshopDialog.PttControllerSource = () => _controller;
        // As MainWindow wires it.
        CheckYourAudioDialog.EndAppKeyForReference = () => (bool)Call("HandleReferenceEndUnkey")!;
    }

    public void Dispose()
    {
        DialogTransmitKeys.HoldWindowLost("test teardown");
        foreach (var name in new[] { "PttEdge", "HoldInFlight", "EscapeUnkey", "ReleaseNow", "SessionOwnsTransmitter" })
            typeof(DialogTransmitKeys).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
        ((System.Windows.Threading.DispatcherTimer?)typeof(MainWindow).GetField("_pttDeferTimer", Any)?.GetValue(_main))?.Stop();
        AudioWorkshopDialog.PttControllerSource = _sourceBefore;
        CheckYourAudioDialog.EndAppKeyForReference = _endBefore;
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        _rig.Dispose();
    }

    // ── plumbing, by name, as DialogTransmitKeysTests ──

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

    private static void AssertTheDeskIsSafe()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    private static PttSafetyController.PttState Hold => PttSafetyController.PttState.PttHold;
    private static PttSafetyController.PttState Idle => PttSafetyController.PttState.Idle;

    private bool Latched => (bool)Get("_pttHoldEndedByApp")!;

    /// <summary>A Ctrl+Space edge, through MainWindow's system-wide door: the same edges every door reaches.</summary>
    private void Edge(bool down) => Call("HandleSystemWidePttEdge", down);

    /// <summary>One JAWS pair: a fresh down, and its up almost at once.</summary>
    private void SyntheticPair()
    {
        Edge(true);
        Edge(false);
    }

    /// <summary>The production deferral timer ran out (its Tick, invoked; no dispatcher is pumped).</summary>
    private void DeferralRunsOut()
    {
        var timer = (System.Windows.Threading.DispatcherTimer?)Get("_pttDeferTimer");
        Assert.True(timer != null, "no deferral timer was armed");
        timer!.Stop();
        var tick = (EventHandler?)typeof(System.Windows.Threading.DispatcherTimer).GetField("Tick", Any)?.GetValue(timer);
        Assert.True(tick != null, "the production timer has no Tick handler");
        tick!(timer, EventArgs.Empty);
    }

    /// <summary>A held Ctrl+Space as JAWS delivers it: pressed, and its first pairs absorbed, still keyed.</summary>
    private void JawsHoldKeyed()
    {
        Edge(true);
        Assert.Equal(Hold, _controller.State);
        Edge(false);                                   // implausibly fast: a synthetic up
        Assert.True(_filter.SynthesisDetected, "positive control: the filter saw the synthetic signature");
        Assert.True(_filter.ReleasePending);
        SyntheticPair();                               // the re-down claims it; the next up defers again
        Assert.Equal(Hold, _controller.State);
        Assert.True(_filter.InRepeatStream, "positive control: the reader's repeat stream is running");
    }

    /// <summary>The reference reached its end and its tail has run: the app lets go of its own key.</summary>
    private void TheReferenceEndsAndTheAppLetsGo()
    {
        Assert.Equal(Holder.App, _dialog.EndTransmissionForReference(_rig, Holder.App, null));
        Assert.Equal(Idle, _controller.State);
    }

    [Fact]
    public void A_JAWS_held_key_across_the_end_does_not_key_the_radio_again()
    {
        AssertTheDeskIsSafe();
        JawsHoldKeyed();
        TheReferenceEndsAndTheAppLetsGo();
        Assert.True(Latched, "the hold the app ended was forgotten, not latched");

        // The operator is still holding the key: JAWS goes on sending pairs.
        for (int i = 0; i < 6; i++)
        {
            SyntheticPair();
            Assert.Equal(Idle, _controller.State);    // the mutation: forget the hold at the end and the first pair keys
        }

        // They let go: the last up is deferred like every other, and no re-down claims it.
        Assert.True(_filter.ReleasePending);
        DeferralRunsOut();
        Assert.False(Latched, "a real release ends the latch");
        Assert.Equal(Idle, _controller.State);

        // The control: a press after that keys as normal.
        Edge(true);
        Assert.Equal(Hold, _controller.State);
    }

    [Fact]
    public void A_synthetic_down_already_on_its_way_when_the_app_lets_go_keys_nothing()
    {
        // The end can land between a synthetic up and its re-down, with a
        // deferral pending. That pending release is the hold's, and it is
        // still the filter's to judge.
        AssertTheDeskIsSafe();
        JawsHoldKeyed();
        Assert.True(_filter.ReleasePending, "positive control: a synthetic up is pending as the end lands");
        TheReferenceEndsAndTheAppLetsGo();
        Edge(true);
        Assert.Equal(Idle, _controller.State);
        Edge(false);
        Assert.Equal(Idle, _controller.State);
        Assert.True(Latched);
    }

    [Fact]
    public void An_NVDA_held_key_across_the_end_does_not_key_again_and_a_release_then_a_press_keys_at_once()
    {
        // NVDA passes the hold through: the first down, then repeats, which
        // the dialog door consumes before the edges. The filter never sees the
        // synthetic signature, so the operator's release ends the latch at once.
        AssertTheDeskIsSafe();
        WireDialogDoor();
        var w = new FakeWindow();
        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Control, false, false));
        Assert.Equal(Hold, _controller.State);
        TheReferenceEndsAndTheAppLetsGo();
        for (int i = 0; i < 6; i++)
        {
            Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Control, true, false));
            Assert.Equal(Idle, _controller.State);
        }
        Assert.False(_filter.SynthesisDetected, "positive control: an NVDA hold never looks synthetic");

        Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 20);
        DialogTransmitKeys.KeyUp(Keys.Space | Keys.Control);
        Assert.False(Latched, "the operator's release under NVDA ends the latch at once");
        Assert.False(_filter.ReleasePending);

        // Inside any defer window there could have been: the press keys.
        Assert.True(DialogTransmitKeys.KeyDown(w, Keys.Space | Keys.Control, false, false));
        Assert.Equal(Hold, _controller.State);
    }

    /// <summary>
    /// The place most worth questioning (the brief's): CAN the latch swallow a
    /// real press? Under JAWS, yes, and in exactly the way the filter already
    /// reads every hold. A real release and a fresh press inside the defer
    /// window look the same as one of the reader's pairs, so the press is read
    /// as the hold continuing and keys nothing; it goes on keying nothing while
    /// the operator holds it, because the reader's pairs keep claiming every
    /// deferral. The first press after a release the timer has confirmed keys.
    /// Inside an ordinary hold the same reading keeps the radio keyed, which is
    /// what the operator meant; here it keeps it unkeyed, which is not. Pinned
    /// as it is, and reported.
    /// </summary>
    [Fact]
    public void Under_JAWS_a_press_inside_the_defer_window_after_a_real_release_is_read_as_the_hold_continuing()
    {
        AssertTheDeskIsSafe();
        JawsHoldKeyed();
        TheReferenceEndsAndTheAppLetsGo();
        Edge(true);
        Edge(false);                                   // the real release: deferred, as every JAWS up is
        Assert.True(_filter.ReleasePending);
        Edge(true);                                    // a fresh press, inside the window
        Assert.Equal(Idle, _controller.State);
        Assert.True(Latched);
        for (int i = 0; i < 3; i++) { Edge(false); Edge(true); }   // held, under JAWS
        Assert.Equal(Idle, _controller.State);

        Edge(false);
        DeferralRunsOut();                             // let go for longer than the window
        Assert.False(Latched);
        Edge(true);
        Assert.Equal(Hold, _controller.State);         // and now it keys
    }

    [Fact]
    public void The_latch_never_outlives_the_input_state_a_disconnect_or_Escape_clears()
    {
        AssertTheDeskIsSafe();
        JawsHoldKeyed();
        TheReferenceEndsAndTheAppLetsGo();
        Assert.True(Latched);

        // Every teardown path (the radio powering off, Escape, a dialog's
        // window loss, the radio ending transmit on its own) calls this.
        Call("ResetPttInput");
        Assert.False(Latched);
        Assert.False(_filter.ReleasePending);

        // So a press after a disconnect and reconnect keys, whatever the reader.
        Edge(true);
        Assert.Equal(Hold, _controller.State);
    }

    [Fact]
    public void A_lock_the_app_lets_go_has_no_held_key_and_is_forgotten_as_before()
    {
        // The control for the scope: only a HOLD is latched.
        AssertTheDeskIsSafe();
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        TheReferenceEndsAndTheAppLetsGo();
        Assert.False(Latched);
        Edge(true);
        Assert.Equal(Hold, _controller.State);
    }

    [Fact]
    public void The_disconnect_path_resets_the_key_edges_input_state()
    {
        // The latch's guarantee rests on the teardown paths calling
        // ResetPttInput; the one this cannot run windowless is the radio
        // powering off, so it is pinned in source.
        AssertTheDeskIsSafe();
        string main = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs"));
        int at = main.IndexOf("private void PowerNowOffInternal()", StringComparison.Ordinal);
        Assert.True(at > 0, "PowerNowOffInternal moved");
        int next = main.IndexOf("\n    private ", at + 10, StringComparison.Ordinal);
        string body = main[at..(next > 0 ? next : main.Length)];
        Assert.Contains("ResetPttInput();", body);
        string reset = main[main.IndexOf("private void ResetPttInput()", StringComparison.Ordinal)..];
        reset = reset[..reset.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("_pttHoldEndedByApp = false;", reset);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "the repository root was not found above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    // ── the dialog door, for the NVDA case ──

    private void WireDialogDoor()
    {
        Door("PttEdge", (Action<bool>)(down => Call("HandleDialogPttEdge", down)));
        Door("HoldInFlight", (Func<bool>)(() => _controller.State == PttSafetyController.PttState.PttHold));
        Door("EscapeUnkey", (Func<bool>)(() => (bool)Call("HandleDialogEscapeUnkey")!));
        Door("ReleaseNow", (Action)(() => Call("HandleDialogHoldLost")));
        Door("SessionOwnsTransmitter", (Func<bool>)(() => false));
    }

    private static void Door(string property, Delegate value)
    {
        var p = typeof(DialogTransmitKeys).GetProperty(property, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(p != null, "production entry DialogTransmitKeys." + property + " is missing");
        p!.SetValue(null, value);
    }

    private sealed class FakeWindow : DialogTransmitKeys.IHoldWindow
    {
        public event EventHandler? Deactivated { add { } remove { } }
        public event EventHandler? Closed { add { } remove { } }
        public bool HandlesAudioCheckEscape => false;
    }
}
