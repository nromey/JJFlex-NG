using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;
using Holder = JJFlexWpf.Dialogs.CheckYourAudioDialog.ReferenceEndHolder;

namespace JJFlexWpf.Tests;

/// <summary>
/// Unified takes, round three (5b): when a reference recording reaches its
/// end, the transmission ends (Noel, 2026-11-04 11:36), including a key the
/// operator holds through the app (18:45). Driven on the real
/// <see cref="PttSafetyController"/> and the real decision in
/// <c>CheckYourAudioDialog.ReferenceEnd.cs</c>.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> No window is constructed: the dialog is
/// <see cref="RuntimeHelpers.GetUninitializedObject"/>, so no constructor
/// runs, no XAML loads, no HWND exists; only its <c>_rig</c> field is set and
/// one internal method called. The test thread is asserted MTA, where WPF
/// refuses to build any element. The rig is an unconnected
/// <see cref="FlexBase"/>, the controller has chirps and speech off, and
/// <see cref="QuietRun"/> has render off. The check's own stop route is a
/// session this test cannot build; that it is the Stop button's call is
/// pinned in source by <c>Radios.Tests.UnifiedTakesReferenceEndTests</c>.
/// </remarks>
public sealed class ReferenceEndTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly CheckYourAudioDialog _dialog;
    private readonly Func<PttSafetyController?>? _sourceBefore;
    private readonly Func<bool>? _endBefore;

    public ReferenceEndTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        _dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
        var rigField = typeof(CheckYourAudioDialog).GetField("_rig", Any);
        Assert.True(rigField != null, "production field CheckYourAudioDialog._rig is missing");
        rigField!.SetValue(_dialog, _rig);
        _sourceBefore = AudioWorkshopDialog.PttControllerSource;
        _endBefore = CheckYourAudioDialog.EndAppKeyForReference;
        AudioWorkshopDialog.PttControllerSource = () => _controller;
        CheckYourAudioDialog.EndAppKeyForReference = null;
    }

    public void Dispose()
    {
        AudioWorkshopDialog.PttControllerSource = _sourceBefore;
        CheckYourAudioDialog.EndAppKeyForReference = _endBefore;
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        _rig.Dispose();
    }

    private static void AssertTheDeskIsSafe()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    private Holder End(Holder planned, FlexBase? rig = null) =>
        _dialog.EndTransmissionForReference(rig ?? _rig, planned, null);

    [Fact]
    public void A_check_is_asked_first_because_a_check_keys_through_the_controller_too()
    {
        AssertTheDeskIsSafe();
        Assert.Equal(Holder.Check, CheckYourAudioDialog.WhoHoldsTheReference(checkKeyed: true, appKeyed: true, radioTransmitting: true));
        Assert.Equal(Holder.App, CheckYourAudioDialog.WhoHoldsTheReference(false, true, true));
        Assert.Equal(Holder.App, CheckYourAudioDialog.WhoHoldsTheReference(false, true, false));
        Assert.Equal(Holder.Elsewhere, CheckYourAudioDialog.WhoHoldsTheReference(false, false, true));
        Assert.Equal(Holder.Nobody, CheckYourAudioDialog.WhoHoldsTheReference(false, false, false));
    }

    [Fact]
    public void The_lock_the_app_holds_is_let_go_through_the_controller()
    {
        AssertTheDeskIsSafe();
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        Assert.Equal(Holder.App, End(Holder.App));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void A_held_push_to_talk_through_the_app_is_let_go_too()
    {
        // Ruled 18:45: the operator's own key through the app unkeys as well.
        AssertTheDeskIsSafe();
        _controller.PttDown();
        Assert.Equal(PttSafetyController.PttState.PttHold, _controller.State);
        Assert.Equal(Holder.App, End(Holder.App));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    /// <summary>
    /// Round five (7a) moved this from <c>DialogTransmitKeys.EscapeUnkey</c>,
    /// which forgot a held key, to MainWindow's <c>HandleReferenceEndUnkey</c>,
    /// which latches it; the end-to-end of that is <see cref="ReferenceEndHeldKeyTests"/>.
    /// </summary>
    [Fact]
    public void With_a_main_window_the_key_edges_let_go_of_the_apps_key()
    {
        AssertTheDeskIsSafe();
        int calls = 0;
        CheckYourAudioDialog.EndAppKeyForReference = () => { calls++; _controller.EscapeUnlock(); return true; };
        _controller.ToggleLock();
        Assert.Equal(Holder.App, End(Holder.App));
        Assert.Equal(1, calls);
        Assert.False(_controller.IsTransmitting);
    }

    [Fact]
    public void A_key_released_during_the_tail_or_another_radio_is_left_alone()
    {
        AssertTheDeskIsSafe();
        // Planned for the app's key, but by the end of the tail nothing holds it.
        Assert.Equal(Holder.Nobody, End(Holder.App));
        // Keyed now, but the reference ended on a radio this window has let go of.
        _controller.ToggleLock();
        using var other = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        Assert.Equal(Holder.Nobody, End(Holder.App, other));
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        // And a check planned, when the app holds the key and no check runs, is not the app's to drop.
        Assert.Equal(Holder.Nobody, End(Holder.Check));
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
    }

    /// <summary>
    /// Ruled 2026-11-05 01:39 to 01:41. The microphone is never back at a
    /// reference's end any more, so no holder hears that it is; only a
    /// hardware line still held is asked to let go.
    /// </summary>
    [Fact]
    public void The_finished_sentence_never_says_the_microphone_is_back_and_asks_only_a_held_line_to_let_go()
    {
        AssertTheDeskIsSafe();
        foreach (var holder in new[] { Holder.Check, Holder.App, Holder.Elsewhere, Holder.Nobody })
            foreach (bool hardware in new[] { false, true })
                Assert.DoesNotContain("microphone", CheckYourAudioDialog.ReferenceFinishedSentence(holder, hardware, VerbosityLevel.Chatty),
                    StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Transmitting of the reference recording is complete.",
            CheckYourAudioDialog.ReferenceFinishedSentence(Holder.App, false, VerbosityLevel.Chatty));
        // Another client's transmission, as far as the app can tell: "release your push to talk" would be false.
        Assert.Equal("Transmitting of the reference recording is complete.",
            CheckYourAudioDialog.ReferenceFinishedSentence(Holder.Elsewhere, false, VerbosityLevel.Chatty));
        Assert.Equal("Transmitting of the reference recording is complete. Release your push to talk to stop transmitting dead air.",
            CheckYourAudioDialog.ReferenceFinishedSentence(Holder.Elsewhere, true, VerbosityLevel.Chatty));
        // A hand mic pressed under the app's own key is still held after the app lets go of its own.
        Assert.Contains("Release your push to talk", CheckYourAudioDialog.ReferenceFinishedSentence(Holder.App, true, VerbosityLevel.Chatty));
        // Nothing transmitting: the last source the radio reported is history.
        Assert.DoesNotContain("Release", CheckYourAudioDialog.ReferenceFinishedSentence(Holder.Nobody, true, VerbosityLevel.Chatty));
    }
}
