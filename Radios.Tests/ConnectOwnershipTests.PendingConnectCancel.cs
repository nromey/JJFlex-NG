using System;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The radio picker's pending connect after a SmartLink pass (#1244 repair 1).
/// Enter on a remembered remote radio before the remote list is live starts a
/// pass and carries the connect; the pass's sign-in window can be cancelled by
/// the operator. That cancel must end the pending connect quietly: "Login
/// cancelled" (said by the sign-in itself) is the only sentence, and nothing
/// says SmartLink could not be reached. A pass that really failed still says
/// so, and a live one still connects.
/// </summary>
/// <remarks>
/// Runs the production <c>DoConnect</c>, <c>TryStartRemoteLookFor</c> and
/// <c>ResumePendingConnect</c> in this class's headless harness: no picker,
/// no window, no sign-in form, no radio. What the window-bound completion
/// lambda in <c>StartRemoteFlow</c> and the VB callbacks hand over is pinned
/// by source in the last test, because neither can run without a window or
/// the app.
/// </remarks>
public sealed partial class ConnectOwnershipTests
{
    private dynamic PickerWithRememberedRemoteRadio()
    {
        lock (TracedLines) TracedLines.Clear();
        dynamic s = Selector(RadioOwnership.Mine);
        s.MakeRememberedRemoteNotListed();
        return s;
    }

    private static System.Collections.Generic.List<string> TracedLines =>
        (System.Collections.Generic.List<string>)SelectorType.Value.Assembly
            .GetType("Headless.Tracing")!.GetField("Lines")!.GetValue(null)!;

    /// <summary>The Enter that starts it all: a pass, and the connect carried.</summary>
    private static void EnterStartsAPassAndCarriesTheConnect(dynamic s)
    {
        s.Connect();
        Assert.Equal(1, (int)s.RemotePasses);
        Assert.Equal(_serial, (string)s.PendingSerial);
        Assert.Equal(new[] { SigningInToLook }, ((System.Collections.Generic.List<string>)s.Spoken).ToArray());
        Assert.Equal(0, (int)s.Completions);
    }

    [Fact]
    public void A_pending_connect_whose_sign_in_the_operator_cancelled_ends_quietly()
    {
        dynamic s = PickerWithRememberedRemoteRadio();
        EnterStartsAPassAndCarriesTheConnect(s);

        s.LandRemotePass(RemotePassOutcome.SignInCancelled);

        // Nothing more is said: not "Could not reach SmartLink to look for".
        Assert.Equal(new[] { SigningInToLook }, ((System.Collections.Generic.List<string>)s.Spoken).ToArray());
        Assert.Null((string)s.PendingSerial);         // the intention is dropped
        Assert.Equal(0, (int)s.Completions);          // no connect
        Assert.Equal(1, (int)s.RemotePasses);         // and no second pass
        Assert.False((bool)s.RemoteListLive);
        lock (TracedLines)
            Assert.Contains(TracedLines, l => l.Contains("the operator cancelled the SmartLink sign-in; the pending connect is dropped", StringComparison.Ordinal));
    }

    /// <summary>Positive control: a pass that really failed still says so, once.</summary>
    [Fact]
    public void A_pending_connect_whose_pass_failed_still_says_SmartLink_could_not_be_reached()
    {
        dynamic s = PickerWithRememberedRemoteRadio();
        EnterStartsAPassAndCarriesTheConnect(s);

        s.LandRemotePass(RemotePassOutcome.NotLive);

        Assert.Equal(new[] { SigningInToLook, Unreachable }, ((System.Collections.Generic.List<string>)s.Spoken).ToArray());
        Assert.Null((string)s.PendingSerial);
        Assert.Equal(0, (int)s.Completions);
        Assert.Equal(1, (int)s.RemotePasses);         // never re-queues on failure
        lock (TracedLines)
            Assert.DoesNotContain(TracedLines, l => l.Contains("the pending connect is dropped", StringComparison.Ordinal));
    }

    /// <summary>Positive control: a live pass that lists the radio still connects, no second Enter.</summary>
    [Fact]
    public void A_pending_connect_whose_pass_went_live_still_connects()
    {
        dynamic s = PickerWithRememberedRemoteRadio();
        EnterStartsAPassAndCarriesTheConnect(s);

        s.ListRadioOnSmartLink();
        s.LandRemotePass(RemotePassOutcome.Live);

        Assert.Equal(1, (int)s.Completions);
        Assert.Null((string)s.PendingSerial);
        Assert.DoesNotContain(Unreachable, (System.Collections.Generic.List<string>)s.Spoken);
    }

    /// <summary>
    /// A cancel ends that one intention and nothing more: the next Enter on
    /// the same radio asks SmartLink again, and that pass's failure speaks.
    /// </summary>
    [Fact]
    public void After_a_cancel_the_next_Enter_starts_a_fresh_pass_and_its_failure_still_speaks()
    {
        dynamic s = PickerWithRememberedRemoteRadio();
        EnterStartsAPassAndCarriesTheConnect(s);
        s.LandRemotePass(RemotePassOutcome.SignInCancelled);

        s.Connect();
        Assert.Equal(2, (int)s.RemotePasses);
        Assert.Equal(_serial, (string)s.PendingSerial);
        s.LandRemotePass(RemotePassOutcome.NotLive);

        Assert.Equal(new[] { SigningInToLook, SigningInToLook, Unreachable },
            ((System.Collections.Generic.List<string>)s.Spoken).ToArray());
    }

    [Fact]
    public void A_pass_is_a_cancel_only_when_the_operator_cancelled_and_the_cancel_comes_first()
    {
        Assert.Equal(RemotePassOutcome.Live, RemotePassOutcomes.For(sessionLive: true, signInCancelledByOperator: false));
        Assert.Equal(RemotePassOutcome.NotLive, RemotePassOutcomes.For(sessionLive: false, signInCancelledByOperator: false));
        Assert.Equal(RemotePassOutcome.SignInCancelled, RemotePassOutcomes.For(sessionLive: false, signInCancelledByOperator: true));
        // The operator said stop: a session that happens to be live does not resume the connect.
        Assert.Equal(RemotePassOutcome.SignInCancelled, RemotePassOutcomes.For(sessionLive: true, signInCancelledByOperator: true));
    }

    /// <summary>
    /// The glue that cannot run without a window or the app: both VB
    /// callbacks hand the rig's outcome (never the session Boolean) to the
    /// picker, and the picker's completion hands that outcome, whole, to the
    /// pending connect and marks the list live only on Live.
    /// </summary>
    [Fact]
    public void The_pickers_discovery_and_refresh_hand_the_outcome_through_to_the_pending_connect()
    {
        string root = RepoRoot();
        string globals = File.ReadAllText(Path.Combine(root, "globals.vb")).Replace("\r\n", "\n");
        foreach (string callback in new[] { ".StartRemoteDiscovery = Sub(onComplete As Action(Of Radios.RemotePassOutcome))",
                                            ".StartRemoteRefresh = Sub(onComplete As Action(Of Radios.RemotePassOutcome))" })
        {
            int at = globals.IndexOf(callback, StringComparison.Ordinal);
            Assert.True(at > 0, callback);
            int end = globals.IndexOf("t.Start()", at, StringComparison.Ordinal);
            string body = globals.Substring(at, end - at);
            Assert.Contains("Dim outcome = RigControl.RemotePassOutcome", body, StringComparison.Ordinal);
            Assert.Contains("onComplete?.Invoke(outcome)", body, StringComparison.Ordinal);
            Assert.DoesNotContain("IsSmartLinkSessionLive", body, StringComparison.Ordinal);
            Assert.Single(body.Split("onComplete?.Invoke(").Skip(1));
        }
        Assert.DoesNotContain("Action(Of Boolean)", globals.Substring(
            globals.IndexOf("Dim callbacks As New JJFlexWpf.Dialogs.RigSelectorCallbacks()", StringComparison.Ordinal)), StringComparison.Ordinal);

        string selector = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "Dialogs", "RigSelectorDialog.xaml.cs")).Replace("\r\n", "\n");
        int flow = selector.IndexOf("private void StartRemoteFlow(", StringComparison.Ordinal);
        int flowEnd = selector.IndexOf("internal static string AccountPassLine(", flow, StringComparison.Ordinal);
        string completion = selector.Substring(flow, flowEnd - flow);
        Assert.Contains("start((outcome) =>", completion, StringComparison.Ordinal);
        Assert.Contains("bool success = outcome == RemotePassOutcome.Live;", completion, StringComparison.Ordinal);
        Assert.Contains("if (hadPending) ResumePendingConnect(outcome);", completion, StringComparison.Ordinal);
        Assert.Single(completion.Split("ResumePendingConnect(").Skip(1));
        Assert.Contains("public required Action<Action<RemotePassOutcome>> StartRemoteDiscovery", selector, StringComparison.Ordinal);
        Assert.Contains("public Action<Action<RemotePassOutcome>>? StartRemoteRefresh", selector, StringComparison.Ordinal);
    }
}
