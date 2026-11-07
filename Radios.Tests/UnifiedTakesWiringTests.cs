#nullable enable
using System;
using System.IO;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes, round two, part 4d: where Check your audio drives a saved
/// playback, pinned in the source because the window itself is never built in
/// a test. Each pin is an ORDER the design depends on, and Play last take's
/// choice is tested as logic.
/// </summary>
public sealed class UnifiedTakesWiringTests
{
    private static string Read(string file) =>
        File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "JJFlexWpf", "Dialogs", file));

    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + signature);
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced: " + signature);
    }

    [Fact]
    public void The_capture_is_armed_before_the_playback_is_asked_for()
    {
        string tick = Body(Read("CheckYourAudioDialog.Check.cs"), "private void TickPostUnkey(FlexBase rig)");
        int arm = tick.IndexOf("if (!TakeReadyForPlayback(rig)) return;", StringComparison.Ordinal);
        int play = tick.IndexOf("_playStart = rig.StartSlicePlayAsync(_recorderTarget);", StringComparison.Ordinal);
        Assert.True(arm > 0 && play > arm, "TakeReadyForPlayback must run before StartSlicePlayAsync");
    }

    [Fact]
    public void A_saved_playback_keeps_the_check_attached_and_drains_before_it_ends()
    {
        string source = Read("CheckYourAudioDialog.Check.cs");
        string tick = Body(source, "private void TickPostUnkey(FlexBase rig)");
        Assert.Contains("if (_method == AudioCheckListenMethods.Loopback || _take?.Capturing == true)", tick);
        Assert.Contains("_take?.End(\"play-refused\");", tick);
        string playing = Body(source, "private void TickPlayingBack(FlexBase rig)");
        int drain = playing.IndexOf("if (!TakeDrained()) return;", StringComparison.Ordinal);
        int end = playing.IndexOf("End(null);", StringComparison.Ordinal);
        Assert.True(drain > 0 && end > drain, "the capture closes before the check ends");
    }

    [Fact]
    public void Looped_back_drains_before_the_arrangement_comes_down()
    {
        string ended = Body(Read("CheckYourAudioDialog.Check.cs"), "private async Task OnSessionEndedAsync()");
        int drain = ended.IndexOf("await DrainEndingTakeAsync(endedRig);", StringComparison.Ordinal);
        int down = ended.IndexOf("EndLoopbackArrangementWithOutcomeAsync()", StringComparison.Ordinal);
        Assert.True(drain > 0 && down > drain, "the take drains before EndLoopbackArrangementWithOutcomeAsync");
    }

    [Fact]
    public void A_radio_change_and_a_teardown_end_the_take_before_the_check_and_never_wait()
    {
        string window = Read("CheckYourAudioDialog.cs");
        string setRig = Body(window, "public void SetRig(FlexBase? rig)");
        int endTake = setRig.IndexOf("_session?.EndTake(", StringComparison.Ordinal);
        int force = setRig.IndexOf("_session?.ForceEnd(", StringComparison.Ordinal);
        Assert.True(endTake > 0 && force > endTake);
        Assert.Contains("_skipTakeDrain = true;", setRig);
        string teardown = Body(window, "public static void EndCheckBeforeTeardown(FlexBase? rig)");
        int tEnd = teardown.IndexOf("inst._session?.EndTake(\"connection-ending\");", StringComparison.Ordinal);
        int tForce = teardown.IndexOf("inst._session?.ForceEnd(", StringComparison.Ordinal);
        Assert.True(tEnd > 0 && tForce > tEnd);
        string drain = Body(Read("CheckYourAudioDialog.SaveTakes.cs"), "private async Task DrainEndingTakeAsync(FlexBase? rig)");
        Assert.Contains("if (_tearingDown || _skipTakeDrain)", drain);
        Assert.Contains("TrackStart(", drain);
    }

    [Fact]
    public void Play_last_take_prefers_the_saved_take_and_falls_back_to_the_radios_slot()
    {
        Assert.Equal(TakePlaybackChoice.Saved, TakePlaybackSelector.Choose(AudioCheckListenMethods.RecordPlayback, true, false, hasSaved: true));
        Assert.Equal(TakePlaybackChoice.Slice, TakePlaybackSelector.Choose(AudioCheckListenMethods.RecordPlayback, true, false, hasSaved: false));
        Assert.Equal(TakePlaybackChoice.Saved, TakePlaybackSelector.Choose(AudioCheckListenMethods.Loopback, false, false, hasSaved: true));
        Assert.Equal(TakePlaybackChoice.None, TakePlaybackSelector.Choose(AudioCheckListenMethods.Loopback, false, false, hasSaved: false));
        Assert.Equal(TakePlaybackChoice.OffAir, TakePlaybackSelector.Choose(AudioCheckListenMethods.OffAir, false, true, hasSaved: true));
    }

    [Fact]
    public void Looped_back_makes_a_take_only_when_its_playback_is_saved()
    {
        Assert.False(AudioCheckMethods.MakesTake(AudioCheckListenMethods.Loopback));
        Assert.False(AudioCheckMethods.MakesTake(AudioCheckListenMethods.Loopback, savedPlayback: false));
        Assert.True(AudioCheckMethods.MakesTake(AudioCheckListenMethods.Loopback, savedPlayback: true));
        Assert.True(AudioCheckMethods.MakesTake(AudioCheckListenMethods.RecordPlayback, savedPlayback: false));
        Assert.False(AudioCheckMethods.MakesTake(AudioCheckListenMethods.Monitor, savedPlayback: true));
        // And a ticked box is not a saved file: the selection checks the disk.
        string saves = Read("CheckYourAudioDialog.SaveTakes.cs");
        Assert.Contains("System.IO.File.Exists(System.IO.Path.Combine(folder, \"take.txt\"))", saves);
    }
}
